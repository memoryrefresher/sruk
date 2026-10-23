using SeaRaider.Core.Catalog;
using SeaRaider.Core.Social;

namespace SeaRaider.App.Services;

/// <summary>
/// Opens a game a friend shared, in this user's own store: the same page if it's in the catalog (same site), else the same
/// game from any of this user's sites (by title), else a store search for it — which asks every source live.
/// </summary>
public sealed class SharedGameOpener(CatalogIndexService index, NavigationService navigation)
{
    public void Open(SharedGame game)
    {
        var byPage = index.FindByPageUrl(game.PageUrl);
        if (byPage is not null)
        {
            navigation.ShowGame(byPage.Id);
            return;
        }

        var byTitle = index.FindByKey(TitleNormalizer.DedupKey(game.Title))
            .OrderByDescending(e => e.DetailsLoaded)
            .ThenByDescending(e => e.UploadedAtUtc)
            .FirstOrDefault();
        if (byTitle is not null)
        {
            navigation.ShowGame(byTitle.Id);
            return;
        }

        navigation.SearchStore(DisplayText.Title(game.Title));
    }
}
