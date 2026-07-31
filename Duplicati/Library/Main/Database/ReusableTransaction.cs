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

using System;
using System.Threading;
using System.Threading.Tasks;
using Duplicati.Library.Utility;
using Microsoft.Data.Sqlite;
using Duplicati.Library.Main.Database.Local;

#nullable enable

namespace Duplicati.Library.Main.Database;

/// <summary>
/// Wraps a transaction so it can be comitted and restarted.
/// </summary>
/// <remarks>
/// Creates a new reusable transaction.
/// </remarks>
internal class ReusableTransaction : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The tag used for logging.
    /// </summary>
    private static readonly string LOGTAG = Logging.Log.LogTagFromType(typeof(ReusableTransaction));

    /// <summary>
    /// The database to use.
    /// </summary>
    private readonly SqliteConnection m_con;
    /// <summary>
    /// The current transaction.
    /// </summary>
    private SqliteTransaction m_transaction;
    /// <summary>
    /// Disposal hook used to preserve a terminal transaction for cleanup retry.
    /// </summary>
    private readonly Func<SqliteTransaction, ValueTask> m_disposeTransactionAsync;
    /// <summary>
    /// True if the transaction is disposed.
    /// </summary>
    private bool m_disposed = false;
    /// <summary>
    /// True after SQLite accepted a terminal commit or rollback for the current handle.
    /// </summary>
    private bool m_transactionCompleted;
    /// <summary>
    /// True after the current terminal transaction handle has been disposed.
    /// </summary>
    private bool m_currentTransactionDisposed;
    /// <summary>
    /// An older, already committed handle retained only for cleanup retry after restart.
    /// </summary>
    private SqliteTransaction? m_cleanupTransaction;
    /// <summary>
    /// True while intermediate commits are being held for an atomic external publication.
    /// </summary>
    private bool m_commitDeferralActive;

    /// <summary>
    /// Creates a reusable transaction over a SQLite connection.
    /// </summary>
    internal ReusableTransaction(SqliteConnection con, SqliteTransaction? transaction = null)
        : this(con, transaction, static value => value.DisposeAsync()) { }

    /// <summary>
    /// Creates a reusable transaction with an injectable disposal operation for deterministic failure tests.
    /// </summary>
    internal ReusableTransaction(
        SqliteConnection con,
        SqliteTransaction? transaction,
        Func<SqliteTransaction, ValueTask> disposeTransactionAsync)
    {
        m_con = con;
        m_transaction = transaction ?? con.BeginTransaction(deferred: true);
        m_disposeTransactionAsync = disposeTransactionAsync;
    }

    /// <summary>
    /// Creates a new reusable transaction.
    /// </summary>
    /// <param name="db">The database this transaction relates to.</param>
    /// <param name="transaction">An optional existing transaction to use. If null, a new transaction is created.</param>
    public ReusableTransaction(LocalDatabase db, SqliteTransaction? transaction = null) : this(db.Connection, transaction) { }

    /// <summary>
    /// The current transaction.
    /// </summary>
    public SqliteTransaction Transaction
        => m_disposed || m_transactionCompleted || m_currentTransactionDisposed
            ? throw new InvalidOperationException("Transaction is completed or disposed")
            : m_transaction;

    /// <summary>
    /// Defers intermediate commits until <see cref="CommitDeferredAsync"/> publishes the transaction.
    /// </summary>
    public void DeferCommits()
    {
        if (m_disposed || m_transactionCompleted)
            throw new InvalidOperationException("Transaction is completed or disposed");
        if (m_commitDeferralActive)
            throw new InvalidOperationException("Commit deferral is already active");

        m_commitDeferralActive = true;
    }

    /// <summary>
    /// Publishes a transaction whose intermediate commits were deferred.
    /// </summary>
    /// <param name="message">The log message to use.</param>
    /// <param name="restart">True if the transaction should be restarted.</param>
    /// <param name="token">A cancellation token.</param>
    public async Task CommitDeferredAsync(string? message = null, bool restart = true, CancellationToken token = default)
    {
        if (m_disposed || m_transactionCompleted)
            throw new InvalidOperationException("Transaction is completed or disposed");
        if (!m_commitDeferralActive)
            throw new InvalidOperationException("Commit deferral is not active");

        await CommitCoreAsync(message, restart, completeDeferral: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits the transaction and restarts it.
    /// </summary>
    /// <param name="token">A cancellation token (currently not observed by this operation).</param>
    /// <returns>A task that completes when the commit is done and a new transaction has been started.</returns>
    public async Task CommitAsync(CancellationToken token)
    {
        await CommitAsync(null, true, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits the transaction asynchronously, optionally logging a timed message and restarting the transaction.
    /// </summary>
    /// <param name="message">The log message to use.</param>
    /// <param name="restart">True if the transaction should be restarted.</param>
    /// <param name="token">A cancellation token (currently not observed by this operation).</param>
    /// <returns>A task that completes when the commit is done and (potentially) a new transaction has been started.</returns>
    /// <exception cref="InvalidOperationException">If the transaction is already Disposed.</exception>
    public async Task CommitAsync(string? message = null, bool restart = true, CancellationToken token = default)
    {
        if (m_disposed || m_transactionCompleted)
            throw new InvalidOperationException("Transaction is completed or disposed");
        if (m_commitDeferralActive)
        {
            if (!restart)
                throw new InvalidOperationException("A deferred transaction can only be ended with CommitDeferredAsync");

            return;
        }

        await CommitCoreAsync(message, restart, completeDeferral: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits the current SQLite transaction and updates commit-deferral state only
    /// after SQLite has durably accepted the commit.
    /// </summary>
    private async Task CommitCoreAsync(string? message, bool restart, bool completeDeferral)
    {
        if (m_cleanupTransaction != null)
        {
            try
            {
                await m_disposeTransactionAsync(m_cleanupTransaction).ConfigureAwait(false);
                m_cleanupTransaction = null;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Cleanup of a previously committed SQLite transaction is still pending.", ex);
            }
        }

        message ??= "Unnamed commit";
        using (var timer = new Logging.Timer(LOGTAG, message, $"CommitTransaction: {message}"))
            await m_transaction.CommitAsync().ConfigureAwait(false);

        var committedTransaction = m_transaction;
        m_transactionCompleted = true;
        if (completeDeferral)
            m_commitDeferralActive = false;

        Exception? cleanupFailure = null;
        try
        {
            await m_disposeTransactionAsync(committedTransaction).ConfigureAwait(false);
            m_currentTransactionDisposed = true;
        }
        catch (Exception ex)
        {
            cleanupFailure = ex;
            Logging.Log.WriteWarningMessage(LOGTAG, "CommittedTransactionDisposeError", ex, "SQLite commit succeeded but transaction cleanup failed; cleanup will be retried later: {0}", ex.Message);
        }

        if (!restart)
        {
            if (cleanupFailure == null)
                m_disposed = true;
            return;
        }

        SqliteTransaction nextTransaction;
        try
        {
            nextTransaction = m_con.BeginTransaction(deferred: true);
        }
        catch (Exception ex) when (completeDeferral)
        {
            // The intended SQLite publication is durable. Preserve its cleanup state and
            // let later database access surface that no replacement transaction exists,
            // rather than misreporting the commit itself as failed.
            Logging.Log.WriteErrorMessage(LOGTAG, "CommittedTransactionRestartError", ex, "SQLite commit succeeded but a replacement transaction could not be started: {0}", ex.Message);
            return;
        }

        if (cleanupFailure != null)
            m_cleanupTransaction = committedTransaction;

        m_transaction = nextTransaction;
        m_transactionCompleted = false;
        m_currentTransactionDisposed = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DisposeAsync().AsTask().Await();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (m_disposed)
            return;

        Exception? failure = null;
        if (!m_transactionCompleted && !m_currentTransactionDisposed)
        {
            try
            {
                using (var timer = new Logging.Timer(LOGTAG, "Dispose", "Rollback during transaction dispose"))
                    await m_transaction.RollbackAsync().ConfigureAwait(false);
                m_transactionCompleted = true;
            }
            catch (Exception ex)
            {
                if (!IsInactiveTransactionException(ex))
                {
                    Logging.Log.WriteErrorMessage(LOGTAG, "ReusableTransaction dispose", ex, "Transaction disposed with error: {0}", ex.Message);
                    failure = ex;
                }
                else
                {
                    m_transactionCompleted = true;
                    Logging.Log.WriteWarningMessage(LOGTAG, "ReusableTransactionAlreadyCompleted", ex, "Transaction was already completed during dispose: {0}", ex.Message);
                }
            }
        }

        if (!m_currentTransactionDisposed)
        {
            try
            {
                await m_disposeTransactionAsync(m_transaction).ConfigureAwait(false);
                m_currentTransactionDisposed = true;
            }
            catch (Exception ex)
            {
                if (!IsInactiveTransactionException(ex))
                    failure = failure == null ? ex : new AggregateException(failure, ex);
                else
                {
                    m_currentTransactionDisposed = true;
                    Logging.Log.WriteWarningMessage(LOGTAG, "ReusableTransactionAlreadyDisposed", ex, "Transaction was already completed before dispose: {0}", ex.Message);
                }
            }
        }

        if (m_cleanupTransaction != null)
        {
            try
            {
                await m_disposeTransactionAsync(m_cleanupTransaction).ConfigureAwait(false);
                m_cleanupTransaction = null;
            }
            catch (Exception ex)
            {
                failure = failure == null ? ex : new AggregateException(failure, ex);
            }
        }

        if (m_currentTransactionDisposed && m_cleanupTransaction == null)
        {
            m_disposed = true;
            m_transactionCompleted = false;
        }

        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Microsoft.Data.Sqlite throws InvalidOperationException ("This SqliteTransaction has completed;
    // it is no longer usable.") when a transaction is rolled back or disposed after it has already
    // been completed. Unfortunately there is nothing specific that can be used to differentiate it
    // from other exceptions that might occur during disposal, so we rely on the exception type, source and message.
    private static bool IsInactiveTransactionException(Exception ex)
        => ex is InvalidOperationException
            && ex.Message.StartsWith("This SqliteTransaction has completed", StringComparison.Ordinal);

    /// <summary>
    /// Rolls back the transaction and restarts it.
    /// </summary>
    /// <param name="token">A cancellation token (currently not observed by this operation).</param>
    /// <returns>A task that completes when the rollback is done and a new transaction has been started.</returns>
    public async Task RollBackAsync(CancellationToken token)
    {
        await RollBackAsync(null, true, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back the transaction and optionally restarts it.
    /// </summary>
    /// <param name="message">Message to log.</param>
    /// <param name="restart">Whether to restart the transaction after rolling back.</param>
    /// <param name="token">A cancellation token (currently not observed by this operation).</param>
    /// <returns>A task that completes when the rollback is done and (potentially) a new transaction has been started.</returns>
    /// <exception cref="InvalidOperationException">If the transaction has already been disposed.</exception>
    public async Task RollBackAsync(string? message = null, bool restart = true, CancellationToken token = default)
    {
        if (m_disposed || m_transactionCompleted)
            throw new InvalidOperationException("Transaction is completed or disposed");

        using (var timer = new Logging.Timer(LOGTAG, message, $"RollbackTransaction: {message}"))
            await m_transaction.RollbackAsync().ConfigureAwait(false);
        m_transactionCompleted = true;
        await m_disposeTransactionAsync(m_transaction).ConfigureAwait(false);
        m_transactionCompleted = false;

        if (!restart)
        {
            m_disposed = true;
            return;
        }

        try
        {
            m_transaction = m_con.BeginTransaction(deferred: true);
        }
        catch
        {
            m_disposed = true;
            throw;
        }
    }

}
