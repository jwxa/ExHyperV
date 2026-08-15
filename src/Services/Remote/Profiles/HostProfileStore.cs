using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ExHyperV.Services.Remote.Profiles;

public sealed class HostProfileStore
{
    public const int CurrentFormatVersion = 1;

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object _sync = new();
    private readonly string _filePath;

    public HostProfileStore(string? filePath = null)
    {
        _filePath = filePath ?? AppDataPaths.HostProfilesFilePath;
        if (string.IsNullOrWhiteSpace(_filePath))
            throw new ArgumentException("主机配置文件路径不能为空。", nameof(filePath));
    }

    public string FilePath => _filePath;

    public IReadOnlyList<HostProfile> Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_filePath)) return Array.Empty<HostProfile>();

            XDocument document = LoadDocument();
            XElement root = document.Root
                ?? throw new InvalidDataException("主机配置文件缺少根节点。");
            if (!string.Equals(root.Name.LocalName, "HostProfiles", StringComparison.Ordinal))
                throw new InvalidDataException("主机配置文件根节点无效。");
            if (!int.TryParse(root.Attribute("version")?.Value, out int version)
                || version != CurrentFormatVersion)
            {
                throw new NotSupportedException($"不支持的主机配置版本：{root.Attribute("version")?.Value ?? "缺失"}。");
            }

            var profiles = root.Elements("Host").Select(ParseProfile).ToArray();
            EnsureUniqueIds(profiles);
            EnsureUniqueAddresses(profiles);
            return profiles;
        }
    }

    private XDocument LoadDocument()
    {
        try
        {
            using XmlReader reader = XmlReader.Create(_filePath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(
                "主机配置文件格式损坏，无法读取。请修复或移走 Hosts.xml 后重试。",
                ex);
        }
    }

    public void Save(IEnumerable<HostProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        HostProfile[] normalized = profiles.Select(HostProfileValidator.ValidateAndNormalize).ToArray();
        EnsureUniqueIds(normalized);
        EnsureUniqueAddresses(normalized);

        var document = new XDocument(
            new XElement(
                "HostProfiles",
                new XAttribute("version", CurrentFormatVersion),
                normalized.Select(ToElement)));

        byte[] content = Serialize(document);
        lock (_sync)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
            if (directory is not null) Directory.CreateDirectory(directory);

            string temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private static HostProfile ParseProfile(XElement element)
    {
        if (!Guid.TryParse(element.Attribute("id")?.Value, out Guid id))
            throw new InvalidDataException("主机配置包含无效 ID。");
        if (!Enum.TryParse(element.Attribute("authentication")?.Value, ignoreCase: false, out HostAuthenticationMode authentication))
            throw new InvalidDataException("主机配置包含无效身份模式。");

        try
        {
            return HostProfileValidator.ValidateAndNormalize(new HostProfile(
                id,
                element.Attribute("name")?.Value ?? string.Empty,
                element.Attribute("address")?.Value ?? string.Empty,
                authentication,
                element.Attribute("userName")?.Value,
                element.Attribute("credentialTarget")?.Value));
        }
        catch (HostProfileValidationException ex)
        {
            throw new InvalidDataException($"主机配置“{id}”无效：{ex.Message}", ex);
        }
    }

    private static XElement ToElement(HostProfile profile)
    {
        var element = new XElement(
            "Host",
            new XAttribute("id", profile.Id.ToString("D")),
            new XAttribute("name", profile.DisplayName),
            new XAttribute("address", profile.Address),
            new XAttribute("authentication", profile.AuthenticationMode));
        if (profile.UserName is not null)
            element.Add(new XAttribute("userName", profile.UserName));
        if (profile.CredentialTarget is not null)
            element.Add(new XAttribute("credentialTarget", profile.CredentialTarget));
        return element;
    }

    private static byte[] Serialize(XDocument document)
    {
        using var stream = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = Utf8WithoutBom,
            Indent = true,
            NewLineChars = Environment.NewLine,
            OmitXmlDeclaration = false
        }))
        {
            document.Save(writer);
        }
        return stream.ToArray();
    }

    private static void EnsureUniqueAddresses(IEnumerable<HostProfile> profiles)
    {
        string? duplicateAddress = profiles
            .GroupBy(profile => profile.Address, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateAddress is not null)
            throw new HostProfileValidationException($"IPv4 地址 {duplicateAddress} 已存在主机配置，请编辑现有配置或使用其他地址。");
    }

    private static void EnsureUniqueIds(IEnumerable<HostProfile> profiles)
    {
        Guid duplicate = profiles.GroupBy(profile => profile.Id).FirstOrDefault(group => group.Count() > 1)?.Key ?? Guid.Empty;
        if (duplicate != Guid.Empty)
            throw new HostProfileValidationException($"主机配置 ID 重复：{duplicate:D}。");
    }
}
