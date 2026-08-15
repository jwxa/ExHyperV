using System.Net;
using System.Net.Sockets;

namespace ExHyperV.Services.Remote.Preflight;

public static class Ipv4Cidr
{
    public static string Normalize(string value)
    {
        if (!TryNormalize(value, out string normalized, out string error))
            throw new FormatException(error);
        return normalized;
    }

    public static bool TryNormalize(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        string input = value?.Trim() ?? string.Empty;
        string[] parts = input.Split('/');
        if (parts.Length != 2)
        {
            error = $"“{input}”不是有效的 IPv4 CIDR，请使用例如 10.0.0.0/24 的格式。";
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out IPAddress? address))
        {
            error = $"“{input}”不是有效的 IPv4 CIDR，请使用例如 10.0.0.0/24 的格式。";
            return false;
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"“{input}”不是 IPv4 CIDR；当前向导不接受主机名或 IPv6。";
            return false;
        }
        if (!int.TryParse(parts[1], out int prefixLength) || prefixLength is < 0 or > 32)
        {
            error = $"“{input}”不是有效的 IPv4 CIDR，请使用 0 到 32 的前缀长度。";
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        uint raw = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        uint network = raw & mask;
        normalized = $"{(network >> 24) & 0xff}.{(network >> 16) & 0xff}.{(network >> 8) & 0xff}.{network & 0xff}/{prefixLength}";
        return true;
    }

    public static bool TryNormalizeFirewallAddress(string? value, out string normalized)
    {
        if (TryNormalize(value, out normalized, out _)) return true;

        string input = value?.Trim() ?? string.Empty;
        string[] parts = input.Split('/');
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[1], out IPAddress? maskAddress)
            || maskAddress.AddressFamily != AddressFamily.InterNetwork)
            return false;

        byte[] maskBytes = maskAddress.GetAddressBytes();
        uint mask = ((uint)maskBytes[0] << 24)
                    | ((uint)maskBytes[1] << 16)
                    | ((uint)maskBytes[2] << 8)
                    | maskBytes[3];
        uint inverse = ~mask;
        if ((inverse & (inverse + 1)) != 0) return false;

        int prefixLength = 0;
        for (uint bit = 0x80000000; (mask & bit) != 0; bit >>= 1) prefixLength++;
        return TryNormalize($"{parts[0]}/{prefixLength}", out normalized, out _);
    }

    public static string ToWindowsFirewallAddress(string cidr)
    {
        string normalized = Normalize(cidr);
        int separator = normalized.IndexOf('/');
        int prefixLength = int.Parse(
            normalized[(separator + 1)..],
            System.Globalization.CultureInfo.InvariantCulture);
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        return $"{normalized[..separator]}/{(mask >> 24) & 0xff}.{(mask >> 16) & 0xff}.{(mask >> 8) & 0xff}.{mask & 0xff}";
    }

    public static bool IsPrivate(string cidr)
    {
        string normalized = Normalize(cidr);
        int separator = normalized.IndexOf('/');
        IPAddress address = IPAddress.Parse(normalized[..separator]);
        int prefixLength = int.Parse(normalized[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        byte[] bytes = address.GetAddressBytes();
        return prefixLength >= 8 && bytes[0] == 10
               || prefixLength >= 12 && bytes[0] == 172 && bytes[1] is >= 16 and <= 31
               || prefixLength >= 16 && bytes[0] == 192 && bytes[1] == 168;
    }
}
