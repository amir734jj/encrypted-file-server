using Api.ViewModels;

namespace Api.Interfaces;

public interface ITemplateService
{
    Task<string> RenderDirectoryListingAsync(DirectoryListingViewModel model);
}