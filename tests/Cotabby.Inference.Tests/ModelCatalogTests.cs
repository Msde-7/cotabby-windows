using Cotabby.Core.Models;

namespace Cotabby.Inference.Tests;

[Collection(ModelDirectoryCollection.Name)]
public sealed class ModelCatalogTests : IDisposable
{
    private readonly ModelDirectory _models = new();

    public void Dispose() => _models.Dispose();

    [Fact]
    public void Available_ListsBuiltInModelsWhenTheFolderIsMissing() =>
        Assert.Equal(ModelCatalog.All, ModelCatalog.Available());

    [Fact]
    public void Available_AddsEveryOtherGgufInTheFolder()
    {
        _models.Write("my-model.Q4.gguf");
        _models.Write(ModelCatalog.All[0].FileName);
        _models.Write("notes.txt");

        var custom = ModelCatalog.Available().Where(m => m.IsCustom).ToList();

        var model = Assert.Single(custom);
        Assert.Equal("my-model.Q4", model.DisplayName);
        Assert.Equal("my-model.Q4.gguf", model.FileName);
        Assert.Equal(model, ModelCatalog.FindById(model.Id));
    }

    [Fact]
    public void FindById_ForgetsACustomModelThatWasRemoved()
    {
        _models.Write("gone.gguf");
        var id = ModelCatalog.Available().Single(m => m.IsCustom).Id;

        File.Delete(Path.Combine(_models.Path, "gone.gguf"));

        Assert.Null(ModelCatalog.FindById(id));
    }
}
