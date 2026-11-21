using System.Xml.Serialization;

namespace Jellyfin.Plugin.SsoAuthentication.Tests;

public sealed class SerializableDictionaryTests
{
    [Fact]
    public void Copying_preserves_entries_without_sharing_mutations()
    {
        var original = new Dictionary<string, Guid> { ["alice"] = Guid.NewGuid() };
        var copy = new SerializableDictionary<string, Guid>(original);

        Assert.Equal(original["alice"], copy["alice"]);
        copy.Clear();
        Assert.Single(original);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("empty")]
    [InlineData("capacity")]
    public void Comparers_control_lookup_and_duplicate_detection(string constructor)
    {
        var original = new Dictionary<string, int> { ["Alice"] = 42 };
        var dictionary = constructor switch
        {
            "copy" => new SerializableDictionary<string, int>(original, StringComparer.OrdinalIgnoreCase),
            "empty" => new SerializableDictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            _ => new SerializableDictionary<string, int>(4, StringComparer.OrdinalIgnoreCase),
        };
        dictionary["Alice"] = 42;

        Assert.Equal(42, dictionary["ALICE"]);
        Assert.Throws<ArgumentException>(() => dictionary.Add("alice", 99));
        Assert.Single(dictionary);
    }

    [Fact]
    public void A_dictionary_with_capacity_round_trips_xml_special_characters_and_multiple_entries()
    {
        var original = new SerializableDictionary<string, string>(2)
        {
            ["alice<&\""] = "value<&\"",
            ["bob"] = "",
        };
        var serializer = new XmlSerializer(original.GetType());
        using var xml = new StringWriter();
        serializer.Serialize(xml, original);
        using var reader = new StringReader(xml.ToString());
        var reloaded = Assert.IsType<SerializableDictionary<string, string>>(serializer.Deserialize(reader));

        Assert.Equal(original.OrderBy(pair => pair.Key), reloaded.OrderBy(pair => pair.Key));
        Assert.Null(original.GetSchema());
    }

    [Theory]
    [InlineData("<dictionary />")]
    [InlineData("<dictionary></dictionary>")]
    public void Both_empty_xml_forms_load(string xml)
    {
        using var reader = new StringReader(xml);
        var serializer = new XmlSerializer(typeof(SerializableDictionary<string, Guid>));

        Assert.Empty(Assert.IsType<SerializableDictionary<string, Guid>>(serializer.Deserialize(reader)));
    }

    [Fact]
    public void Duplicate_keys_in_xml_are_rejected_instead_of_silently_replacing_links()
    {
        const string item = "<item><key><string>alice</string></key><value><int>1</int></value></item>";
        using var reader = new StringReader("<dictionary>" + item + item + "</dictionary>");
        var serializer = new XmlSerializer(typeof(SerializableDictionary<string, int>));

        var error = Assert.Throws<InvalidOperationException>(() => serializer.Deserialize(reader));

        Assert.IsType<ArgumentException>(error.InnerException);
    }
}
