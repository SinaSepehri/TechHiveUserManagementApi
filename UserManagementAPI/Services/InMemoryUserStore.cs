using UserManagementAPI.Models;

namespace UserManagementAPI.Services;

/// <summary>
/// Thread-safe in-memory user store.
///
/// Performance notes:
/// - Users are kept in a SortedDictionary (ordered by ID), so pages come back in a stable order
///   and lookups by ID are O(log n).
/// - A separate email index makes the duplicate-email check O(1) instead of scanning every user.
/// - A ReaderWriterLockSlim lets many GET requests read in parallel; only writes are exclusive.
/// </summary>
public sealed class InMemoryUserStore : IUserStore
{
    private readonly SortedDictionary<int, User> _users = new();
    private readonly Dictionary<string, int> _idByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReaderWriterLockSlim _lock = new();
    private int _nextId = 1;

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _users.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public (IReadOnlyList<User> Items, int Total) GetPage(int skip, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        _lock.EnterReadLock();
        try
        {
            return (_users.Values.Skip(skip).Take(take).ToArray(), _users.Count);
        }
        finally { _lock.ExitReadLock(); }
    }

    public User? GetById(int id)
    {
        _lock.EnterReadLock();
        try { return _users.GetValueOrDefault(id); }
        finally { _lock.ExitReadLock(); }
    }

    public User Create(UserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = Normalize(0, request);

        _lock.EnterWriteLock();
        try
        {
            if (_idByEmail.ContainsKey(user.Email))
            {
                throw new DuplicateEmailException(user.Email);
            }

            user = user with { Id = _nextId++ };
            _users.Add(user.Id, user);
            _idByEmail.Add(user.Email, user.Id);
            return user;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public User? Update(int id, UserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var updated = Normalize(id, request);

        _lock.EnterWriteLock();
        try
        {
            if (!_users.TryGetValue(id, out var existing))
            {
                return null;
            }

            // The email may stay the same, but must not belong to a *different* user.
            if (_idByEmail.TryGetValue(updated.Email, out var ownerId) && ownerId != id)
            {
                throw new DuplicateEmailException(updated.Email);
            }

            _idByEmail.Remove(existing.Email);
            _idByEmail[updated.Email] = id;
            _users[id] = updated;
            return updated;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Delete(int id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_users.Remove(id, out var removed))
            {
                return false;
            }

            _idByEmail.Remove(removed.Email);
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Trims all fields and stores emails in lower case, so " Avery@X.com " and "avery@x.com" are the same user.</summary>
    private static User Normalize(int id, UserRequest request) => new(
        id,
        request.FirstName.Trim(),
        request.LastName.Trim(),
        request.Email.Trim().ToLowerInvariant(),
        request.Department.Trim());
}
