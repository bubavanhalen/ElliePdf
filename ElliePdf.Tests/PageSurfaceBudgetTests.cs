using System.Collections.Specialized;
using Xunit;

namespace ElliePdf.Tests;

public sealed class PageSurfaceBudgetTests
{
    [Fact]
    public void TenThousand_requests_never_exceed_the_twelve_surface_policy()
    {
        var budget = new PageSurfaceBudget();

        for (var page = 0; page < 10_000; page++)
        {
            budget.Request(page);
            Assert.InRange(budget.ActiveCount, 0, PageSurfaceBudget.MaximumCapacity);
        }

        Assert.Equal(PageSurfaceBudget.MaximumCapacity, budget.ActiveCount);
        Assert.Equal(10_000 - PageSurfaceBudget.MaximumCapacity, budget.PendingCount);
    }

    [Fact]
    public void Release_promotes_pending_pages_in_order_and_skips_cancelled_pages()
    {
        var budget = new PageSurfaceBudget(2);
        Assert.True(budget.Request(1));
        Assert.True(budget.Request(2));
        Assert.False(budget.Request(3));
        Assert.False(budget.Request(4));
        Assert.Null(budget.Release(3));

        Assert.Equal(4, budget.Release(1));
        Assert.Equal(2, budget.ActiveCount);
        Assert.Contains(4, budget.ActivePages);
    }

    [Fact]
    public void Bulk_collection_replaces_ten_thousand_items_with_one_reset()
    {
        var collection = new BulkObservableCollection<int>();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, args) => notifications.Add(args);

        collection.ReplaceAll(Enumerable.Range(0, 10_000));

        Assert.Equal(10_000, collection.Count);
        var notification = Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, notification.Action);
    }
}
