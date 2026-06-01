using Api.Interfaces;
using Api.ViewModels;
using RazorLight;

namespace Api.Services;

public sealed class TemplateService : ITemplateService
{
    private readonly RazorLightEngine _engine = new RazorLightEngineBuilder()
        .UseEmbeddedResourcesProject(typeof(TemplateService).Assembly, "Api.Templates")
        .UseMemoryCachingProvider()
        .Build();

    public async Task<string> RenderDirectoryListingAsync(DirectoryListingViewModel model)
    {
        return await _engine.CompileRenderAsync("DirectoryListing", model);
    }
}
