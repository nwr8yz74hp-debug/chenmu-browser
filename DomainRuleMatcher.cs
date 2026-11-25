using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ChenmuBrowser
{
    internal static class DomainRuleMatcher
    {
        internal static string NormalizeHost(string host)
        {
            var normalized = (host ?? String.Empty).Trim().TrimStart('.').TrimEnd('.');
            if (normalized.Length == 0 || normalized.Contains("/") || normalized.Contains("\\") ||
                normalized.Contains("@") || normalized.Any(Char.IsWhiteSpace))
                return String.Empty;
            if (normalized.Length >= 2 && normalized[0] == '[' && normalized[normalized.Length - 1] == ']')
                normalized = normalized.Substring(1, normalized.Length - 2);
            var hostType = Uri.CheckHostName(normalized);
            if (hostType == UriHostNameType.Unknown)
                return String.Empty;
            if (hostType == UriHostNameType.Dns)
            {
                try { normalized = new IdnMapping().GetAscii(normalized); }
                catch { return String.Empty; }
            }
            return normalized.ToLowerInvariant();
        }

        internal static bool Matches(string requestHost, string ruleHost)
        {
            var request = NormalizeHost(requestHost);
            var rule = NormalizeHost(ruleHost);
            if (request.Length == 0 || rule.Length == 0)
                return false;
            return String.Equals(request, rule, StringComparison.OrdinalIgnoreCase) ||
                request.EndsWith("." + rule, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldBlock(string requestHost, IEnumerable<string> blockedHosts, IEnumerable<string> allowedHosts)
        {
            if (blockedHosts == null)
                return false;
            if (allowedHosts != null)
                foreach (var allowed in allowedHosts)
                    if (Matches(requestHost, allowed))
                        return false;
            foreach (var blocked in blockedHosts)
                if (Matches(requestHost, blocked))
                    return true;
            return false;
        }
    }
}
