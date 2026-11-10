using System.Diagnostics;
using UserManagementAPI.Models;
using UserManagementAPI.Services;
using Xunit.Abstractions;

namespace UserManagementAPI.Tests;

public sealed class InMemoryUserStoreTests(ITestOutputHelper output)
{
    [Fact]
    public void Create_assigns_increasing_ids()
    {
        var store = new InMemoryUserStore();
        Assert.Equal(1, store.Create(NewUser("first@techhive.example")).Id);
        Assert.Equal(2, store.Create(NewUser("second@techhive.example")).Id);
    }

    [Fact]
    public void Create_trims_fields_and_lowercases_email()
    {
        var store = new InMemoryUserStore();
        var user = store.Create(new CreateUserRequest
        {
            FirstName = "  Avery ", LastName = " Morgan", Email = "  Avery@TechHive.Example ", Department = " Engineering "
        });

        Assert.Equal(new User(1, "Avery", "Morgan", "avery@techhive.example", "Engineering"), user);
    }

    [Theory]
    [InlineData("avery@techhive.example")]
    [InlineData("AVERY@techhive.example")]
    [InlineData("  avery@techhive.example  ")]
    public void Create_rejects_duplicate_email(string secondEmail)
    {
        var store = new InMemoryUserStore();
        store.Create(NewUser("avery@techhive.example"));

        Assert.Throws<DuplicateEmailException>(() => store.Create(NewUser(secondEmail)));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Update_changes_an_existing_user_and_can_keep_its_email()
    {
        var store = new InMemoryUserStore();
        var created = store.Create(NewUser("avery@techhive.example"));

        var updated = store.Update(created.Id, new UpdateUserRequest
        {
            FirstName = "Avery", LastName = "Morgan", Email = "AVERY@techhive.example", Department = "Platform Engineering"
        });

        Assert.NotNull(updated);
        Assert.Equal("Platform Engineering", updated.Department);
    }

    [Fact]
    public void Update_rejects_another_users_email()
    {
        var store = new InMemoryUserStore();
        store.Create(NewUser("first@techhive.example"));
        var second = store.Create(NewUser("second@techhive.example"));

        Assert.Throws<DuplicateEmailException>(() => store.Update(second.Id, UpdateTo("first@techhive.example")));
        Assert.Equal("second@techhive.example", store.GetById(second.Id)!.Email);
    }

    [Fact]
    public void Update_releases_the_old_email()
    {
        var store = new InMemoryUserStore();
        var user = store.Create(NewUser("old@techhive.example"));
        store.Update(user.Id, UpdateTo("new@techhive.example"));

        // The old address is free again.
        Assert.Equal(2, store.Create(NewUser("old@techhive.example")).Id);
    }

    [Fact]
    public void Delete_removes_the_user_and_frees_the_email()
    {
        var store = new InMemoryUserStore();
        var created = store.Create(NewUser("avery@techhive.example"));

        Assert.True(store.Delete(created.Id));
        Assert.Null(store.GetById(created.Id));
        Assert.False(store.Delete(created.Id));
        store.Create(NewUser("avery@techhive.example")); // no DuplicateEmailException
    }

    [Fact]
    public void Missing_users_return_null_or_false()
    {
        var store = new InMemoryUserStore();
        Assert.Null(store.GetById(404));
        Assert.Null(store.Update(404, UpdateTo("x@techhive.example")));
        Assert.False(store.Delete(404));
    }

    [Fact]
    public void GetPage_returns_users_in_id_order_with_total()
    {
        var store = new InMemoryUserStore();
        for (var i = 1; i <= 5; i++) store.Create(NewUser($"user{i}@techhive.example"));

        var (items, total) = store.GetPage(skip: 2, take: 2);

        Assert.Equal(5, total);
        Assert.Equal([3, 4], items.Select(u => u.Id));
        Assert.Empty(store.GetPage(10, 2).Items);
    }

    [Fact]
    public void GetPage_rejects_invalid_arguments()
    {
        var store = new InMemoryUserStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetPage(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetPage(0, 0));
    }

    [Fact]
    public async Task Concurrent_creates_get_unique_ids()
    {
        var store = new InMemoryUserStore();

        await Parallel.ForEachAsync(Enumerable.Range(0, 1_000), (i, _) =>
        {
            store.Create(NewUser($"user{i}@techhive.example"));
            return ValueTask.CompletedTask;
        });

        var (items, total) = store.GetPage(0, 1_000);
        Assert.Equal(1_000, total);
        Assert.Equal(1_000, items.Select(u => u.Id).Distinct().Count());
    }

    [Fact]
    public void Stays_fast_with_many_users()
    {
        var store = new InMemoryUserStore();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++) store.Create(NewUser($"user{i}@techhive.example"));
        var createMs = sw.ElapsedMilliseconds;

        sw.Restart();
        for (var i = 0; i < 1_000; i++) store.GetPage(0, 50);
        var pageMs = sw.ElapsedMilliseconds;

        output.WriteLine($"20,000 creates: {createMs} ms; 1,000 first-page reads: {pageMs} ms");
        // The duplicate-email check is an O(1) index lookup, not a scan of every user.
        Assert.True(createMs < 2_000, $"Creates took {createMs} ms");
        Assert.True(pageMs < 1_000, $"Page reads took {pageMs} ms");
    }

    private static CreateUserRequest NewUser(string email) => new()
    {
        FirstName = "Avery", LastName = "Morgan", Email = email, Department = "Engineering"
    };

    private static UpdateUserRequest UpdateTo(string email) => new()
    {
        FirstName = "Avery", LastName = "Morgan", Email = email, Department = "Engineering"
    };
}
