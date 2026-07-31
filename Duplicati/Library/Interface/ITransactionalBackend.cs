// Copyright (C) 2026, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Duplicati.Library.Interface;

/// <summary>
/// An optional backend capability for grouping remote operations into a transaction.
/// </summary>
/// <remarks>
/// <para>
/// A transaction is ambient to the backend instance that creates it. After
/// <see cref="BeginTransactionAsync"/> succeeds and until the transaction reaches a terminal state,
/// every inherited <see cref="IBackend"/> operation invoked on that exact instance belongs to the
/// transaction. Callers must keep using that instance. All inherited backend operations,
/// terminal calls, transaction disposal, and disposal of the owning backend instance must be
/// serialized with one another. The owning backend must not be disposed before its transaction.
/// </para>
/// <para>
/// Only one transaction may be active on an instance. Implementations must throw
/// <see cref="InvalidOperationException"/> when a caller attempts to begin a nested or overlapping
/// transaction. A cancelled or failed begin must leave the instance without an active transaction.
/// Read-write changes must not become visible outside the transaction before a successful commit.
/// Backends that do not implement this interface retain the existing non-transactional behavior.
/// </para>
/// </remarks>
public interface ITransactionalBackend : IBackend
{
    /// <summary>
    /// Begins an instance-bound backend transaction for one Duplicati operation.
    /// </summary>
    /// <param name="context">Versioned metadata describing the Duplicati operation.</param>
    /// <param name="cancellationToken">Token to cancel transaction creation.</param>
    /// <returns>The handle for the active transaction on this backend instance.</returns>
    Task<IBackendTransaction> BeginTransactionAsync(
        BackendTransactionContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Controls the terminal state of an instance-bound backend transaction.
/// </summary>
/// <remarks>
/// <para>
/// Commit, rollback, and disposal must not run concurrently. A successful commit is terminal and a
/// repeated commit is a no-op; a successful rollback is terminal and a repeated rollback is a no-op.
/// Attempting to commit after rollback or explicitly roll back after commit must throw
/// <see cref="InvalidOperationException"/>. A failed terminal call leaves the transaction active so
/// the caller can attempt rollback or disposal.
/// </para>
/// <para>
/// <see cref="IAsyncDisposable.DisposeAsync"/> must never commit. When the transaction is still
/// active it must perform and await the equivalent of
/// <c>RollbackAsync(null, CancellationToken.None)</c>; after a terminal call it is an idempotent no-op.
/// Disposal may propagate a rollback failure.
/// </para>
/// </remarks>
public interface IBackendTransaction : IAsyncDisposable
{
    /// <summary>
    /// Publishes all remote changes made in the transaction.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel before irreversible publication begins.</param>
    /// <remarks>
    /// Once irreversible publication begins, an implementation must finish determining the commit
    /// result rather than abandon publication merely because this token is subsequently cancelled.
    /// </remarks>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Abandons all unpublished remote changes made in the transaction.
    /// </summary>
    /// <param name="exception">The exception that caused the rollback, if any.</param>
    /// <param name="cancellationToken">Token to cancel rollback resource cleanup.</param>
    Task RollbackAsync(Exception? exception, CancellationToken cancellationToken);
}

/// <summary>
/// Describes whether a backend transaction may mutate remote state.
/// </summary>
public enum BackendTransactionMode
{
    /// <summary>
    /// The operation only reads remote state.
    /// </summary>
    ReadOnly = 0,

    /// <summary>
    /// The operation may mutate remote state.
    /// </summary>
    ReadWrite = 1
}

/// <summary>
/// Versioned, extensible metadata for a backend transaction.
/// </summary>
/// <remarks>
/// Future contract revisions must remain optional for implementations of version 1. An
/// implementation must reject an unsupported <see cref="ContractVersion"/> with
/// <see cref="NotSupportedException"/> before starting a transaction rather than silently
/// interpreting newer metadata with older semantics.
/// </remarks>
public sealed class BackendTransactionContext
{
    private int contractVersion = 1;
    private Guid operationId;
    private string operationName = string.Empty;
    private BackendTransactionMode mode;
    private DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
    private IReadOnlyDictionary<string, string> extensions =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>
    /// Gets the transaction context contract version.
    /// </summary>
    public int ContractVersion
    {
        get => contractVersion;
        init => contractVersion = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The contract version must be positive.");
    }

    /// <summary>
    /// Gets the unique identifier for this Duplicati operation.
    /// </summary>
    public required Guid OperationId
    {
        get => operationId;
        init => operationId = value != Guid.Empty
            ? value
            : throw new ArgumentException("The operation identifier cannot be empty.", nameof(value));
    }

    /// <summary>
    /// Gets the stable English name of the Duplicati operation.
    /// </summary>
    public required string OperationName
    {
        get => operationName;
        init => operationName = !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("The operation name cannot be empty.", nameof(value));
    }

    /// <summary>
    /// Gets the transaction access mode.
    /// </summary>
    public required BackendTransactionMode Mode
    {
        get => mode;
        init => mode = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The transaction mode is not supported.");
    }

    /// <summary>
    /// Gets the non-default UTC timestamp at which the operation started.
    /// </summary>
    public DateTimeOffset StartedAtUtc
    {
        get => startedAtUtc;
        init
        {
            if (value == default)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The operation start time cannot be the default value.");
            if (value.Offset != TimeSpan.Zero)
                throw new ArgumentException("The operation start time must use the UTC offset.", nameof(value));

            startedAtUtc = value;
        }
    }

    /// <summary>
    /// Gets an immutable snapshot of optional backend-specific context values.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extensions
    {
        get => extensions;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            extensions = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(value, StringComparer.Ordinal));
        }
    }
}
