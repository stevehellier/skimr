using Microsoft.EntityFrameworkCore;
using Skimr.Domain;
using Skimr.Infrastructure.Fetching;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Reading;

public sealed record BoardDetails(long Id, string Name, int Count);

/// <summary>
/// Boards: a user's own collections. An article can only be added if it's already in one of
/// the user's lists, and only to the user's own boards. Boarded articles survive unsubscribing
/// and retention (PLAN D41, D52).
/// </summary>
public sealed class BoardService(IDbContextFactory<SkimrDbContext> contexts, TimeProvider time)
{
    public async Task<IReadOnlyList<BoardDetails>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Boards.Where(b => b.UserId == userId).OrderBy(b => b.SortOrder).ThenBy(b => b.Name)
            .Select(b => new BoardDetails(b.Id, b.Name, b.Articles.Count)).ToListAsync(cancellationToken);
    }

    public async Task<LibraryResult> CreateAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        var clean = StoredText.Fit(name?.Trim(), 100);
        if (string.IsNullOrEmpty(clean))
        {
            return LibraryResult.Fail("Give the board a name.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.Boards.AnyAsync(b => b.UserId == userId && b.Name == clean, cancellationToken))
        {
            return LibraryResult.Fail("You already have a board with that name.");
        }

        var order = (await db.Boards.Where(b => b.UserId == userId).MaxAsync(b => (int?)b.SortOrder, cancellationToken) ?? -1) + 1;
        var board = new Board { UserId = userId, Name = clean, SortOrder = order };
        db.Boards.Add(board);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return LibraryResult.Fail("You already have a board with that name.");
        }

        return LibraryResult.Ok(board.Id);
    }

    public async Task<LibraryResult> RenameAsync(Guid userId, long boardId, string name, CancellationToken cancellationToken)
    {
        var clean = StoredText.Fit(name?.Trim(), 100);
        if (string.IsNullOrEmpty(clean))
        {
            return LibraryResult.Fail("Give the board a name.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var board = await db.Boards.SingleOrDefaultAsync(b => b.Id == boardId && b.UserId == userId, cancellationToken);
        if (board is null)
        {
            return LibraryResult.Missing("Board not found.");
        }

        board.Name = clean;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return LibraryResult.Fail("You already have a board with that name.");
        }

        return LibraryResult.Ok(boardId);
    }

    public async Task<LibraryResult> DeleteAsync(Guid userId, long boardId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Boards.Where(b => b.Id == boardId && b.UserId == userId).ExecuteDeleteAsync(cancellationToken) > 0
            ? LibraryResult.Ok(boardId)
            : LibraryResult.Missing("Board not found.");
    }

    /// <summary>The ids of this user's boards that hold the article.</summary>
    public async Task<IReadOnlySet<long>> BoardsForArticleAsync(Guid userId, long articleId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return (await db.BoardArticles.Where(ba => ba.ArticleId == articleId && ba.Board!.UserId == userId).Select(ba => ba.BoardId).ToListAsync(cancellationToken)).ToHashSet();
    }

    public async Task<LibraryResult> AddAsync(Guid userId, long boardId, long articleId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var allowed = await db.Boards.AnyAsync(b => b.Id == boardId && b.UserId == userId, cancellationToken)
            && await db.UserArticles.AnyAsync(u => u.UserId == userId && u.ArticleId == articleId, cancellationToken);
        if (!allowed)
        {
            return LibraryResult.Missing("Board or article not found.");
        }

        if (await db.BoardArticles.AnyAsync(ba => ba.BoardId == boardId && ba.ArticleId == articleId, cancellationToken))
        {
            return LibraryResult.Ok(boardId);
        }

        db.BoardArticles.Add(new BoardArticle { BoardId = boardId, ArticleId = articleId, AddedAt = time.GetUtcNow().UtcDateTime });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Added by a double-click a moment ago: it's there, which is what was asked.
        }

        return LibraryResult.Ok(boardId);
    }

    public async Task<LibraryResult> RemoveAsync(Guid userId, long boardId, long articleId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.BoardArticles.Where(ba => ba.BoardId == boardId && ba.ArticleId == articleId && ba.Board!.UserId == userId).ExecuteDeleteAsync(cancellationToken) == 0
            && !await db.Boards.AnyAsync(b => b.Id == boardId && b.UserId == userId, cancellationToken))
        {
            return LibraryResult.Missing("Board not found.");
        }

        return LibraryResult.Ok(boardId);
    }
}
