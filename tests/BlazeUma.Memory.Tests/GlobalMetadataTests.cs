using BlazeUma.Memory.Il2Cpp;

namespace BlazeUma.Memory.Tests;

/// <summary>Integration checks against the installed game's metadata; they pass trivially when the game is not installed.</summary>
public class GlobalMetadataTests
{
    private const string Installed =
        @"C:\Program Files (x86)\Steam\steamapps\common\UmamusumePrettyDerby\UmamusumePrettyDerby_Data\il2cpp_data\Metadata\global-metadata.dat";

    [Theory]
    [InlineData("Gallop.WorkDataManager")]
    [InlineData("Gallop.WorkSingleModeCharaData")]
    [InlineData("Gallop.SceneManager")]
    [InlineData("Gallop.StoryManager")]
    [InlineData("CodeStage.AntiCheat.ObscuredTypes.ObscuredInt")]
    public void FindsTypesTheReaderDependsOn(string name)
    {
        if (!File.Exists(Installed)) return;
        var metadata = new GlobalMetadata(Installed);
        int index = metadata.FindType(name);
        Assert.True(index >= 0, $"{name} missing");
        Assert.Equal(name, metadata.QualifiedName(index));
    }

    [Fact]
    public void RejectsNonMetadataFiles()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[256]);
            Assert.Throws<UnsupportedLayoutException>(() => new GlobalMetadata(path));
        }
        finally { File.Delete(path); }
    }
}
