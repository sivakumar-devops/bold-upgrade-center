using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Bold.UpgradeCenter.Services;

public sealed class ObjectStorageXmlRepository(IUpgradeCenterObjectStore storage) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var keys = storage.ListAsync("data-protection-keys/").GetAwaiter().GetResult();
        var elements = new List<XElement>();
        foreach (var key in keys)
        {
            var content = storage.ReadAsync(key.Key).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("An Upgrade Center authentication key disappeared during loading.");
            try
            {
                using var input = new MemoryStream(content);
                using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
                elements.Add(XElement.Load(reader));
            }
            catch (XmlException)
            {
                throw new InvalidOperationException("An Upgrade Center authentication key is invalid. Restore its dedicated key storage before continuing.");
            }
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)));
        storage.WriteAsync($"data-protection-keys/{Guid.NewGuid():N}.xml", content, createOnly: true).GetAwaiter().GetResult();
    }
}
