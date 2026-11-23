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
        public long ExpectedSize { get; set; }
    }

    internal static class UpdateService
    {
        private const string RepositoryOwner = "nwr8yz74hp-debug";
        private const string RepositoryName = "chenmu-browser";
        private const string InstallerAssetName = "CHENMU-Browser-Setup-x64.exe";
        private const long MaximumInstallerBytes = 120L * 1024L * 1024L;
        private static readonly string LatestReleaseUrl =
            "https://api.github.com/repos/" + RepositoryOwner + "/" + RepositoryName + "/releases/latest";
        private static readonly string InstallerPathPrefix =
            "/" + RepositoryOwner + "/" + RepositoryName + "/releases/download/";

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
            using (var client = CreateClient(true, false, TimeSpan.FromSeconds(12)))
            using (var response = await client.GetAsync(LatestReleaseUrl).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var release = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(json) as Dictionary<string, object>;
                if (release == null)
                    throw new InvalidDataException("更新服务器返回的数据无法识别。");

                var tag = ReadString(release, "tag_name");
                Version version;
                if (!TryParseVersion(tag, out version))
                    throw new InvalidDataException("更新版本号格式无效；正式版本需使用 vX.Y.Z 格式。");

                object assetsValue;
                var assets = release.TryGetValue("assets", out assetsValue) ? assetsValue as object[] : null;
                var asset = assets == null
                    ? null
                    : assets.OfType<Dictionary<string, object>>()
                        .FirstOrDefault(item => String.Equals(ReadString(item, "name"), InstallerAssetName, StringComparison.Ordinal));
                if (asset == null)
                    throw new InvalidDataException("最新版本中没有找到 Windows x64 安装包。");

                object sizeValue;
                long assetSize;
                if (!asset.TryGetValue("size", out sizeValue) ||
                    !Int64.TryParse(Convert.ToString(sizeValue, CultureInfo.InvariantCulture), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out assetSize) ||
                    assetSize <= 0 || assetSize > MaximumInstallerBytes)
                    throw new InvalidDataException("更新安装包大小无效或超出安全限制。");

                var downloadUrl = ReadString(asset, "browser_download_url");
                Uri uri;
                var expectedAssetPath = InstallerPathPrefix + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(InstallerAssetName);
                if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out uri) ||
                    !String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(uri.AbsolutePath, expectedAssetPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新下载地址不属于已配置的公开 GitHub Releases 源。");

                var digest = ReadString(asset, "digest");
                const string prefix = "sha256:";
                if (String.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("发布包缺少 SHA-256 校验摘要，已停止更新以保护安全。");
                var sha256 = digest.Substring(prefix.Length).Trim();
                if (sha256.Length != 64 || sha256.Any(ch => !Uri.IsHexDigit(ch)))
                    throw new InvalidDataException("发布包的 SHA-256 校验摘要格式无效。");

                return new UpdateInfo
                {
                    Version = version,
                    Tag = tag,
                    DownloadUrl = uri.AbsoluteUri,
                    Sha256 = sha256.ToLowerInvariant(),
                    ExpectedSize = assetSize
                };
            }
        }

        public static async Task<string> DownloadAndVerifyAsync(UpdateInfo update)
        {
            if (update == null)
                throw new ArgumentNullException("update");

            var outputPath = Path.Combine(Path.GetTempPath(), "CHENMU-Browser-Setup-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                using (var client = CreateClient(false, false, TimeSpan.FromMinutes(5)))
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
                    if (total == 0 || total != update.ExpectedSize)
                        throw new InvalidDataException("下载文件大小与 GitHub Release 声明不一致。");
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

        public static void LaunchInstallerAfterExit(string installerPath, int processId, string installDirectory)
        {
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("找不到已下载的更新安装包。", installerPath);

            var scriptPath = Path.Combine(Path.GetTempPath(), "chenmu-browser-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            var currentExe = Path.Combine(installDirectory, "CHENMU Browser.exe");
            var script =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$targetPid = " + processId.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "$installer = " + PowerShellLiteral(installerPath) + "\r\n" +
                "$installDir = " + PowerShellLiteral(installDirectory) + "\r\n" +
                "$app = " + PowerShellLiteral(currentExe) + "\r\n" +
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
                "  $installArgs = '/S /D=' + $installDir\r\n" +
                "  $result = Start-Process -FilePath $installer -ArgumentList $installArgs -Wait -PassThru\r\n" +
                "  Add-Content -LiteralPath $logPath -Value ('Installer exit code: ' + $result.ExitCode)\r\n" +
                "  if ($result.ExitCode -ne 0) { throw ('安装程序退出代码：' + $result.ExitCode) }\r\n" +
                "  if (Test-Path -LiteralPath $app) { Start-Process -FilePath $app }\r\n" +
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

        private static HttpClient CreateClient(bool apiRequest, bool allowRedirect, TimeSpan timeout)
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = allowRedirect });
            client.Timeout = timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CHENMU-Browser/" + CurrentVersion.ToString());
            if (apiRequest)
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
            }
            return client;
        }

        private static string ReadString(IDictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : String.Empty;
        }

        private static bool TryParseVersion(string tag, out Version version)
        {
            version = null;
            if (String.IsNullOrWhiteSpace(tag))
                return false;
            var value = tag.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(1);
            var suffix = value.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0)
                value = value.Substring(0, suffix);
            return Version.TryParse(value, out version) && version.Build >= 0;
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
