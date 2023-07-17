using LeaderBoard.SharedKernel.Contracts.Domain;
using LeaderBoard.SharedKernel.OptimisticConcurrency;

namespace LeaderBoard.SharedKernel.EventStoreDB.Repository;

public class EventStoreDBRepositoryWithETagDecorator<T>: IEventStoreDBRepository<T>
    where T : class, IAggregate
{
    private readonly IEventStoreDBRepository<T> inner;
    private readonly IExpectedResourceVersionProvider expectedResourceVersionProvider;
    private readonly INextResourceVersionProvider nextResourceVersionProvider;

    public EventStoreDBRepositoryWithETagDecorator(
        IEventStoreDBRepository<T> inner,
        IExpectedResourceVersionProvider expectedResourceVersionProvider,
        INextResourceVersionProvider nextResourceVersionProvider
    )
    {
        this.inner = inner;
        this.expectedResourceVersionProvider = expectedResourceVersionProvider;
        this.nextResourceVersionProvider = nextResourceVersionProvider;
    }

    public Task<T?> Find(Guid id, CancellationToken cancellationToken) =>
        inner.Find(id, cancellationToken);

    public async Task<ulong> Add(T aggregate, CancellationToken cancellationToken = default)
    {
        var nextExpectedVersion = await inner.Add(
            aggregate,
            cancellationToken
        ).ConfigureAwait(true);

        nextResourceVersionProvider.TrySet(nextExpectedVersion.ToString());

        return nextExpectedVersion;
    }

    public async Task<ulong> Update(T aggregate, ulong? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var nextExpectedVersion = await inner.Update(
            aggregate,
            expectedVersion ?? GetExpectedVersion(),
            cancellationToken
        ).ConfigureAwait(true);

        nextResourceVersionProvider.TrySet(nextExpectedVersion.ToString());

        return nextExpectedVersion;
    }

    public async Task<ulong> Delete(T aggregate, ulong? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        var nextExpectedVersion = await inner.Delete(
            aggregate,
            expectedVersion ?? GetExpectedVersion(),
            cancellationToken
        ).ConfigureAwait(true);

        nextResourceVersionProvider.TrySet(nextExpectedVersion.ToString());

        return nextExpectedVersion;
    }

    private ulong? GetExpectedVersion()
    {
        var value = expectedResourceVersionProvider.Value;

        if (string.IsNullOrWhiteSpace(value) || !ulong.TryParse(value, out var expectedVersion))
            return null;

        return expectedVersion;
    }
}
