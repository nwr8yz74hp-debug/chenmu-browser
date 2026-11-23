using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ChenmuBrowser
{
    internal sealed class UpdateInfo
    {
        public Version Version { get; set; }
        public string Tag { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
    }

    internal static class UpdateService
    {
        private const string RepositoryOwner = "nwr8yz74hp-debug";
        private const string RepositoryName = "chenmu-browser";
        private const string InstallerAssetName = "CHENMU-Browser-Setup-x64.exe";
        private const string ChecksumsAssetName = "SHA256SUMS.txt";
        private const int MaximumChecksumManifestBytes = 64 * 1024;
        private const long MaximumInstallerBytes = 120L * 1024L * 1024L;
        private static readonly string LatestReleaseUrl =
            "https://github.com/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
        private static readonly string ReleaseAssetPathPrefix =
            "/" + RepositoryOwner + "/" + RepositoryName + "/releases/download/";
        private static readonly string LatestReleaseTagPathPrefix =
            "/" + RepositoryOwner + "/" + RepositoryName + "/releases/tag/";

        public static Version CurrentVersion
        {
            get
            {
                var version = typeof(UpdateService).Assembly.GetName().Version;
                return version == null
                    ? new Version(1, 0, 0)
                    : new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            }
        }

        public static bool IsInstalledDirectory(string directory)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\CHENMU Browser"))
                {
                    var installed = key == null ? null : key.GetValue("InstallDir") as string;
                    if (String.IsNullOrWhiteSpace(installed) || String.IsNullOrWhiteSpace(directory))
                        return false;
                    return String.Equals(Path.GetFullPath(installed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        public static async Task<UpdateInfo> GetLatestReleaseAsync()
        {
            using (var client = CreateClient(TimeSpan.FromSeconds(12)))
            using (var latestResponse = await GetWithHttpsRedirectsAsync(client, new Uri(LatestReleaseUrl)).ConfigureAwait(false))
            {
                if (latestResponse.StatusCode == HttpStatusCode.NotFound)
                    return null;
                latestResponse.EnsureSuccessStatusCode();

                var latestUri = latestResponse.RequestMessage == null ? null : latestResponse.RequestMessage.RequestUri;
                var tag = ExtractLatestTag(latestUri);
                Version version;
                if (!TryParseVersion(tag, out version))
                    throw new InvalidDataException("更新版本号格式无效；正式版本需使用 vX.Y.Z 格式。");

                var downloadUri = BuildReleaseAssetUri(tag, InstallerAssetName);
                var checksumsUri = BuildReleaseAssetUri(tag, ChecksumsAssetName);
                using (var checksumResponse = await GetWithHttpsRedirectsAsync(client, checksumsUri).ConfigureAwait(false))
                {
                    if (checksumResponse.StatusCode == HttpStatusCode.NotFound)
                        throw new InvalidDataException("最新版本中没有找到 SHA-256 校验清单。");
                    checksumResponse.EnsureSuccessStatusCode();
                    var contentLength = checksumResponse.Content.Headers.ContentLength;
                    if (contentLength.HasValue && (contentLength.Value <= 0 || contentLength.Value > MaximumChecksumManifestBytes))
                        throw new InvalidDataException("SHA-256 校验清单超出安全大小限制。");
                    var manifest = await ReadSmallTextAsync(checksumResponse.Content, MaximumChecksumManifestBytes).ConfigureAwait(false);
                    var sha256 = ReadSha256FromManifest(manifest, InstallerAssetName);
                    return new UpdateInfo
                    {
                        Version = version,
                        Tag = tag,
                        DownloadUrl = downloadUri.AbsoluteUri,
                        Sha256 = sha256
                    };
                }
            }
        }

        public static async Task<string> DownloadAndVerifyAsync(UpdateInfo update)
        {
            if (update == null)
                throw new ArgumentNullException("update");

            var outputPath = Path.Combine(Path.GetTempPath(), "CHENMU-Browser-Setup-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                using (var client = CreateClient(TimeSpan.FromMinutes(5)))
                using (var response = await GetWithHttpsRedirectsAsync(client, new Uri(update.DownloadUrl)).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var declaredLength = response.Content.Headers.ContentLength;
                    if (declaredLength.HasValue && (declaredLength.Value <= 0 || declaredLength.Value > MaximumInstallerBytes))
                        throw new InvalidDataException("更新安装包大小无效或超出安全限制。");

                    long total = 0;
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var target = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            total += read;
                            if (total > MaximumInstallerBytes)
                                throw new InvalidDataException("更新安装包超出安全大小限制。");
                            await target.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                        }
                    }
                    if (total == 0 || (declaredLength.HasValue && total != declaredLength.Value))
                        throw new InvalidDataException("下载文件大小与服务器响应不一致。");
                }

                string actualHash;
                using (var stream = File.OpenRead(outputPath))
                using (var sha = SHA256.Create())
                    actualHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty).ToLowerInvariant();

                if (!String.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包的 SHA-256 校验失败；未运行安装程序。");

                return outputPath;
            }
            catch
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); }
                catch { }
                throw;
            }
        }

        public static void LaunchInstallerAfterExit(string installerPath, int processId, string installDirectory, Version expectedVersion)
        {
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("找不到已下载的更新安装包。", installerPath);
            if (expectedVersion == null)
                throw new ArgumentNullException("expectedVersion");

            var scriptPath = Path.Combine(Path.GetTempPath(), "chenmu-browser-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            var currentExe = Path.Combine(installDirectory, "CHENMU Browser.exe");
            var script =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$targetPid = " + processId.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "$installer = " + PowerShellLiteral(installerPath) + "\r\n" +
                "$installDir = " + PowerShellLiteral(installDirectory) + "\r\n" +
                "$app = " + PowerShellLiteral(currentExe) + "\r\n" +
                "$expectedVersion = " + PowerShellLiteral(expectedVersion.ToString(3)) + "\r\n" +
                "$expectedFileVersion = " + PowerShellLiteral(expectedVersion.ToString(3) + ".0") + "\r\n" +
                "$registryKey = 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\CHENMU Browser'\r\n" +
                "$logDir = Join-Path $env:LOCALAPPDATA 'CHENMU Browser'\r\n" +
                "$logPath = Join-Path $logDir 'update-install.log'\r\n" +
                "$deadline = (Get-Date).AddMinutes(2)\r\n" +
                "try {\r\n" +
                "  New-Item -ItemType Directory -Path $logDir -Force | Out-Null\r\n" +
                "  Add-Content -LiteralPath $logPath -Value ('Waiting for browser exit: ' + (Get-Date -Format o))\r\n" +
                "  while (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) {\r\n" +
                "    if ((Get-Date) -gt $deadline) { throw '浏览器未能及时关闭，更新已取消。' }\r\n" +
                "    Start-Sleep -Milliseconds 500\r\n" +
                "  }\r\n" +
                "  Add-Content -LiteralPath $logPath -Value ('Starting silent installer: ' + (Get-Date -Format o))\r\n" +
                "  # NSIS requires /D to be last and unquoted; it accepts spaces through end-of-command-line.\r\n" +
                "  $startInfo = New-Object System.Diagnostics.ProcessStartInfo\r\n" +
                "  $startInfo.FileName = $installer\r\n" +
                "  $startInfo.Arguments = '/S /D=' + $installDir\r\n" +
                "  $startInfo.UseShellExecute = $false\r\n" +
                "  $startInfo.CreateNoWindow = $true\r\n" +
                "  $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden\r\n" +
                "  $result = [System.Diagnostics.Process]::Start($startInfo)\r\n" +
                "  if ($null -eq $result) { throw '无法启动更新安装程序。' }\r\n" +
                "  $result.WaitForExit()\r\n" +
                "  Add-Content -LiteralPath $logPath -Value ('Installer exit code: ' + $result.ExitCode)\r\n" +
                "  if ($result.ExitCode -ne 0) { throw ('安装程序退出代码：' + $result.ExitCode) }\r\n" +
                "  if (-not (Test-Path -LiteralPath $app)) { throw '安装后未找到浏览器程序。' }\r\n" +
                "  $installed = Get-ItemProperty -LiteralPath $registryKey -Name DisplayVersion,InstallLocation\r\n" +
                "  if ($installed.DisplayVersion -ne $expectedVersion) { throw ('安装版本不符：' + $installed.DisplayVersion) }\r\n" +
                "  $trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)\r\n" +
                "  $actualInstallDir = [System.IO.Path]::GetFullPath($installed.InstallLocation).TrimEnd($trimChars)\r\n" +
                "  $expectedInstallDir = [System.IO.Path]::GetFullPath($installDir).TrimEnd($trimChars)\r\n" +
                "  if (-not [string]::Equals($actualInstallDir, $expectedInstallDir, [System.StringComparison]::OrdinalIgnoreCase)) { throw '安装目录与原目录不一致。' }\r\n" +
                "  $actualFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion\r\n" +
                "  if ($actualFileVersion -ne $expectedFileVersion) { throw ('程序文件版本不符：' + $actualFileVersion) }\r\n" +
                "  Add-Content -LiteralPath $logPath -Value ('Verified installed version ' + $actualFileVersion + ' at ' + $actualInstallDir)\r\n" +
                "  Start-Process -FilePath $app\r\n" +
                "} catch {\r\n" +
                "  try { Add-Content -LiteralPath $logPath -Value ('Update failed: ' + $_.Exception.Message) } catch {}\r\n" +
                "  Add-Type -AssemblyName System.Windows.Forms\r\n" +
                "  [System.Windows.Forms.MessageBox]::Show(('自动更新失败：' + $_.Exception.Message), 'CHENMU Browser', 'OK', 'Error') | Out-Null\r\n" +
                "  if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $app)) { Start-Process -FilePath $app }\r\n" +
                "} finally {\r\n" +
                "  Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue\r\n" +
                "  Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue\r\n" +
                "}\r\n";
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));

            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell))
            {
                File.Delete(scriptPath);
                throw new FileNotFoundException("找不到 Windows PowerShell，无法完成自动更新。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " + QuoteWindowsArgument(scriptPath),
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                Process.Start(startInfo);
            }
            catch
            {
                try { File.Delete(scriptPath); }
                catch { }
                throw;
            }
        }

        private static async Task<HttpResponseMessage> GetWithHttpsRedirectsAsync(HttpClient client, Uri startUri)
        {
            var current = startUri;
            for (var hop = 0; hop < 6; hop++)
            {
                if (!String.Equals(current.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !current.IsDefaultPort || !String.IsNullOrEmpty(current.UserInfo) ||
                    !IsAllowedDownloadHost(current.Host))
                    throw new InvalidDataException("更新下载发生不安全的地址跳转。");

                using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                {
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!IsRedirect(response.StatusCode))
                        return response;

                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location == null)
                        throw new InvalidDataException("更新下载重定向缺少目标地址。");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                }
            }
            throw new InvalidDataException("更新下载的重定向次数超出限制。");
        }

        private static string ExtractLatestTag(Uri latestUri)
        {
            if (latestUri == null ||
                !String.Equals(latestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !latestUri.IsDefaultPort || !String.IsNullOrEmpty(latestUri.UserInfo) ||
                !String.Equals(latestUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !String.IsNullOrEmpty(latestUri.Query) || !String.IsNullOrEmpty(latestUri.Fragment))
                throw new InvalidDataException("无法从公开 GitHub Release 链接识别版本。");

            var path = latestUri.AbsolutePath;
            if (!path.StartsWith(LatestReleaseTagPathPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub 最新 Release 重定向地址不属于已配置的仓库。");

            var encodedTag = path.Substring(LatestReleaseTagPathPrefix.Length);
            var tag = Uri.UnescapeDataString(encodedTag);
            Version ignored;
            if (String.IsNullOrWhiteSpace(tag) || tag.IndexOf('/') >= 0 || tag.IndexOf('\\') >= 0 ||
                !TryParseVersion(tag, out ignored))
                throw new InvalidDataException("更新版本号格式无效；正式版本需使用 vX.Y.Z 格式。");
            return tag;
        }

        private static Uri BuildReleaseAssetUri(string tag, string assetName)
        {
            Version ignored;
            if (!TryParseVersion(tag, out ignored) ||
                (!String.Equals(assetName, InstallerAssetName, StringComparison.Ordinal) &&
                 !String.Equals(assetName, ChecksumsAssetName, StringComparison.Ordinal)))
                throw new InvalidDataException("更新文件名称或版本标签无效。");

            var uri = new Uri("https://github.com/" + RepositoryOwner + "/" + RepositoryName +
                "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(assetName));
            var expectedPath = ReleaseAssetPathPrefix + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(assetName);
            if (!String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新下载地址不属于已配置的公开 GitHub Releases 源。");
            return uri;
        }

        private static string ReadSha256FromManifest(string manifest, string assetName)
        {
            if (String.IsNullOrWhiteSpace(manifest) || Encoding.UTF8.GetByteCount(manifest) > MaximumChecksumManifestBytes)
                throw new InvalidDataException("SHA-256 校验清单为空或超出安全大小限制。");

            string sha256 = null;
            foreach (var line in manifest.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !String.Equals(parts[1], assetName, StringComparison.Ordinal))
                    continue;
                if (parts.Length != 2 || sha256 != null || parts[0].Length != 64 || parts[0].Any(ch => !Uri.IsHexDigit(ch)))
                    throw new InvalidDataException("SHA-256 校验清单中安装包摘要格式无效。");
                sha256 = parts[0].ToLowerInvariant();
            }

            if (sha256 == null)
                throw new InvalidDataException("SHA-256 校验清单中没有 Windows x64 安装包摘要。");
            return sha256;
        }

        private static async Task<string> ReadSmallTextAsync(HttpContent content, int maximumBytes)
        {
            using (var source = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var target = new MemoryStream())
            {
                var buffer = new byte[4096];
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    if (target.Length + read > maximumBytes)
                        throw new InvalidDataException("SHA-256 校验清单超出安全大小限制。");
                    target.Write(buffer, 0, read);
                }
                return Encoding.UTF8.GetString(target.ToArray());
            }
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 301 || code == 302 || code == 303 || code == 307 || code == 308;
        }

        private static bool IsAllowedDownloadHost(string host)
        {
            return String.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            client.Timeout = timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CHENMU-Browser/" + CurrentVersion.ToString());
            return client;
        }

        private static bool TryParseVersion(string tag, out Version version)
        {
            version = null;
            if (String.IsNullOrWhiteSpace(tag) || !tag.StartsWith("v", StringComparison.Ordinal))
                return false;
            var value = tag.Substring(1);
            var components = value.Split('.');
            if (components.Length != 3 || components.Any(component => String.IsNullOrEmpty(component) ||
                component.Any(ch => ch < '0' || ch > '9')))
                return false;
            return Version.TryParse(value, out version) && version.Build >= 0 &&
                String.Equals(version.ToString(3), value, StringComparison.Ordinal);
        }

        private static string PowerShellLiteral(string value)
        {
            return "'" + (value ?? String.Empty).Replace("'", "''") + "'";
        }

        private static string QuoteWindowsArgument(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
