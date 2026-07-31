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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duplicati.Library.DynamicLoader;
using Duplicati.Library.Interface;
using Duplicati.Library.Main;
using Duplicati.Library.Main.Backend;
using Duplicati.Library.Main.Database;
using Duplicati.Library.Main.Operation;
using Duplicati.Library.Utility;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace Duplicati.UnitTest;

[TestFixture]
[NonParallelizable]
public sealed class BackendTransactionManagerTests
{
    [OneTimeSetUp]
    public void RegisterBackend()
        => BackendLoader.AddBackend(new TransactionalProbeBackend());

    [SetUp]
    public void ResetBackend()
        => TransactionalProbeBackend.Reset();

    [Test]
    public async Task NonTransactionalBackendRetainsLegacyBehavior()
    {
        using var target = new TempFolder();
        var results = new BackupResults();
        using var manager = CreateManager("file://" + (string)target, results);

        var started = await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None);

        Assert.That(started, Is.False);
        Assert.That(await manager.ListAsync(null, CancellationToken.None), Is.Empty);
    }

    [Test]
    public async Task TransactionUsesOneInstanceAndSerializesConcurrentDownloads()
    {
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        var downloads = new[]
        {
            manager.GetDirectAsync("first", string.Empty, -1, false, CancellationToken.None),
            manager.GetDirectAsync("second", string.Empty, -1, false, CancellationToken.None),
        };

        var files = await Task.WhenAll(downloads);
        foreach (var file in files)
            file.Dispose();

        await manager.CommitTransactionAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.MaxConcurrentOperations, Is.EqualTo(1));
            Assert.That(
                TransactionalProbeBackend.OperationInstanceIds,
                Is.All.EqualTo(TransactionalProbeBackend.TransactionInstanceId));
            Assert.That(TransactionalProbeBackend.CommitCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.Zero);
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task NestedBeginFailsWithoutLosingActiveTransaction()
    {
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        Assert.That(
            async () => await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());

        var cause = new InvalidOperationException("test rollback");
        await manager.RollbackTransactionAsync(cause, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.CommitCalls, Is.Zero);
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.RollbackException, Is.SameAs(cause));
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task NullTransactionHandleDoesNotRetainDisposedBackend()
    {
        TransactionalProbeBackend.ReturnNullTransaction = true;
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            async () => await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());

        Assert.DoesNotThrowAsync(
            async () => await manager.ListAsync(null, CancellationToken.None));
        Assert.That(TransactionalProbeBackend.OperationsOnDisposedBackend, Is.Zero);
    }

    [Test]
    public async Task FailedCommitCanBeRolledBackWithoutStoppingHandler()
    {
        TransactionalProbeBackend.FailCommit = true;
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        Assert.That(
            async () => await manager.CommitTransactionAsync(CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());

        var cause = new InvalidOperationException("commit failed");
        await manager.RollbackTransactionAsync(cause, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.CommitCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.RollbackException, Is.SameAs(cause));
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SuccessfulCommitWithFailedHandleDisposePublishesAndRetriesCleanupBeforeNextBegin()
    {
        TransactionalProbeBackend.FailTransactionDispose = true;
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        Assert.DoesNotThrowAsync(
            async () => await manager.CommitTransactionAsync(CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.CommitCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.Zero);
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.Zero);
        });

        TransactionalProbeBackend.FailTransactionDispose = false;
        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);
        await manager.RollbackTransactionAsync(null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(3));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task FailedRollbackAndDisposeRetainTransactionForCleanupRetry()
    {
        TransactionalProbeBackend.FailRollback = true;
        TransactionalProbeBackend.FailTransactionDispose = true;
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        Assert.That(
            async () => await manager.RollbackTransactionAsync(
                new InvalidOperationException("first cleanup attempt"),
                CancellationToken.None),
            Throws.TypeOf<AggregateException>());
        Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.Zero);

        TransactionalProbeBackend.FailRollback = false;
        TransactionalProbeBackend.FailTransactionDispose = false;

        Assert.DoesNotThrowAsync(
            async () => await manager.RollbackTransactionAsync(
                new InvalidOperationException("cleanup retry"),
                CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(2));
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(2));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task FailedRollbackWithSuccessfulDisposeClearsTransactionAfterReportingFailure()
    {
        TransactionalProbeBackend.RollbackFailuresRemaining = 1;
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        Assert.That(
            async () => await manager.RollbackTransactionAsync(
                new InvalidOperationException("rollback fails once"),
                CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(2));
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(1));
        });
        Assert.That(
            async () => await manager.RollbackTransactionAsync(
                new InvalidOperationException("transaction is already cleared"),
                CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task SuccessfulRollbackWithFailedDisposeRetainsTransactionForCleanupRetry()
    {
        TransactionalProbeBackend.FailTransactionDispose = true;
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        Assert.That(
            async () => await manager.RollbackTransactionAsync(
                new InvalidOperationException("dispose fails"),
                CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.Zero);

        TransactionalProbeBackend.FailTransactionDispose = false;
        Assert.DoesNotThrowAsync(
            async () => await manager.RollbackTransactionAsync(
                new InvalidOperationException("cleanup retry"),
                CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(2));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task FailedTransactionOwnerDisposeIsExposedAndRetriedBeforeNextBegin()
    {
        TransactionalProbeBackend.BackendDisposeFailuresRemaining = 1;
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None), Is.True);
        Assert.That(
            async () => await manager.RollbackTransactionAsync(null, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>());

        Assert.That(await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None), Is.True);
        await manager.RollbackTransactionAsync(null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.BackendDisposeAttempts, Is.EqualTo(3));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RollbackIgnoresAnAlreadyCanceledTransferToken()
    {
        var results = new BackupResults();
        using var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        results.TaskControl.Terminate();

        Assert.DoesNotThrowAsync(
            async () => await manager.RollbackTransactionAsync(
                new OperationCanceledException("test cancellation"),
                CancellationToken.None));
        Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task OperationCancellationDuringRetryDelayKeepsHandlerAvailableForRollback()
    {
        TransactionalProbeBackend.FailDelete = true;
        var results = new BackupResults();
        using var manager = CreateManager(
            $"{TransactionalProbeBackend.Key}://target",
            results,
            numberOfRetries: "3",
            retryDelay: "1m");
        using var operationCancellation = new CancellationTokenSource();

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        var delete = manager.DeleteAsync("retry-delete", -1, true, operationCancellation.Token);
        using (var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (results.BackendWriter.RetryAttempts == 0)
                await Task.Delay(10, retryTimeout.Token);
        operationCancellation.Cancel();

        Assert.That(
            async () => await delete.WaitAsync(TimeSpan.FromSeconds(5)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.DoesNotThrowAsync(
            async () => await manager.RollbackTransactionAsync(
                new OperationCanceledException("retry delay canceled"),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.DeleteAttempts, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TaskControlCancellationDuringRetryDelayKeepsHandlerAvailableForRollback()
    {
        TransactionalProbeBackend.FailDelete = true;
        var results = new BackupResults();
        using var manager = CreateManager(
            $"{TransactionalProbeBackend.Key}://target",
            results,
            numberOfRetries: "3",
            retryDelay: "1m");

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        var delete = manager.DeleteAsync("retry-delete", -1, true, CancellationToken.None);
        using (var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (results.BackendWriter.RetryAttempts == 0)
                await Task.Delay(10, retryTimeout.Token);
        results.TaskControl.Terminate();

        Assert.That(
            async () => await delete.WaitAsync(TimeSpan.FromSeconds(5)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.DoesNotThrowAsync(
            async () => await manager.RollbackTransactionAsync(
                new OperationCanceledException("task control canceled retry delay"),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.DeleteAttempts, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DisposingManagerRollsBackAndDisposesAnActiveTransactionOnce()
    {
        var results = new BackupResults();
        var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        manager.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.CommitCalls, Is.Zero);
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DisposingManagerRetriesTransientTransactionCleanupFailure()
    {
        TransactionalProbeBackend.TransactionDisposeFailuresRemaining = 1;
        var results = new BackupResults();
        var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None), Is.True);
        manager.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(2));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DisposingManagerPreservesBackendWhenTransactionCleanupFails()
    {
        var results = new BackupResults();
        var manager = CreateManager($"{TransactionalProbeBackend.Key}://target", results);

        Assert.That(
            await manager.BeginTransactionAsync(CreateContext(), CancellationToken.None),
            Is.True);

        TransactionalProbeBackend.FailRollback = true;
        TransactionalProbeBackend.FailTransactionDispose = true;
        manager.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(TransactionalProbeBackend.RollbackCalls, Is.EqualTo(2));
            Assert.That(TransactionalProbeBackend.TransactionDisposeCalls, Is.EqualTo(2));
            Assert.That(TransactionalProbeBackend.TransactionBackendDisposeCalls, Is.Zero);
        });
    }

    private static BackendManager CreateManager(
        string url,
        BackupResults results,
        string numberOfRetries = "0",
        string retryDelay = "10s")
        => new(
            url,
            new Options(new Dictionary<string, string?>
            {
                ["asynchronous-upload-limit"] = "4",
                ["restore-volume-downloaders"] = "4",
                ["number-of-retries"] = numberOfRetries,
                ["retry-delay"] = retryDelay,
                ["no-encryption"] = "true",
            }),
            results.BackendWriter,
            results.TaskControl);

    private static BackendTransactionContext CreateContext()
        => new()
        {
            OperationId = Guid.NewGuid(),
            OperationName = "Backup",
            Mode = BackendTransactionMode.ReadWrite,
        };

    private sealed class TransactionalProbeBackend : ITransactionalBackend, IStreamingBackend, IFolderEnabledBackend
    {
        public const string Key = "transaction-probe";
        private static int nextInstanceId;
        private static int activeOperations;
        private static int maxConcurrentOperations;
        private bool transactionActive;
        private bool disposed;
        private readonly int instanceId;

        public TransactionalProbeBackend()
            => instanceId = Interlocked.Increment(ref nextInstanceId);

        public TransactionalProbeBackend(string url, Dictionary<string, string> options)
            => instanceId = Interlocked.Increment(ref nextInstanceId);

        public static bool FailCommit { get; set; }
        public static bool FailDelete { get; set; }
        public static bool FailRollback { get; set; }
        public static int RollbackFailuresRemaining { get; set; }
        public static bool FailTransactionDispose { get; set; }
        public static int TransactionDisposeFailuresRemaining { get; set; }
        public static int BackendDisposeFailuresRemaining { get; set; }
        public static bool ReturnNullTransaction { get; set; }
        public static int TransactionInstanceId { get; private set; }
        public static int CommitCalls { get; private set; }
        public static int DeleteAttempts { get; private set; }
        public static int RollbackCalls { get; private set; }
        public static int TransactionDisposeCalls { get; private set; }
        public static int TransactionBackendDisposeCalls { get; private set; }
        public static int BackendDisposeAttempts { get; private set; }
        public static int OperationsOnDisposedBackend { get; private set; }
        public static Exception? RollbackException { get; private set; }
        public static ConcurrentBag<int> OperationInstanceIds { get; } = [];
        public static int MaxConcurrentOperations => Volatile.Read(ref maxConcurrentOperations);

        public string DisplayName => "Transactional probe backend";
        public string ProtocolKey => Key;
        public string Description => "Backend transaction manager test double";
        public IList<ICommandLineArgument> SupportedCommands => [];
        public bool SupportsStreaming => true;

        public static void Reset()
        {
            nextInstanceId = 0;
            activeOperations = 0;
            maxConcurrentOperations = 0;
            FailCommit = false;
            FailDelete = false;
            FailRollback = false;
            RollbackFailuresRemaining = 0;
            FailTransactionDispose = false;
            TransactionDisposeFailuresRemaining = 0;
            BackendDisposeFailuresRemaining = 0;
            ReturnNullTransaction = false;
            TransactionInstanceId = 0;
            CommitCalls = 0;
            DeleteAttempts = 0;
            RollbackCalls = 0;
            TransactionDisposeCalls = 0;
            TransactionBackendDisposeCalls = 0;
            BackendDisposeAttempts = 0;
            OperationsOnDisposedBackend = 0;
            RollbackException = null;
            OperationInstanceIds.Clear();
        }

        public Task<IBackendTransaction> BeginTransactionAsync(
            BackendTransactionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (transactionActive)
                throw new InvalidOperationException("A transaction is already active.");

            transactionActive = true;
            TransactionInstanceId = instanceId;
            if (ReturnNullTransaction)
                return Task.FromResult<IBackendTransaction>(null!);
            return Task.FromResult<IBackendTransaction>(new ProbeTransaction(this));
        }

        public async Task GetAsync(string remotename, Stream stream, CancellationToken cancellationToken)
        {
            if (!transactionActive)
                throw new InvalidOperationException("The operation is not in the active transaction.");

            OperationInstanceIds.Add(instanceId);
            var concurrent = Interlocked.Increment(ref activeOperations);
            UpdateMaximum(concurrent);
            try
            {
                await Task.Delay(40, cancellationToken);
                var data = Encoding.UTF8.GetBytes(remotename);
                await stream.WriteAsync(data, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeOperations);
            }
        }

        public Task PutAsync(string remotename, Stream stream, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public async Task GetAsync(string remotename, string filename, CancellationToken cancellationToken)
        {
            await using var stream = File.Create(filename);
            await GetAsync(remotename, stream, cancellationToken);
        }

        public Task PutAsync(string remotename, string filename, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DeleteAsync(string remotename, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!transactionActive)
                throw new InvalidOperationException("The operation is not in the active transaction.");

            DeleteAttempts++;
            if (FailDelete)
                throw new IOException("Simulated delete failure.");

            return Task.CompletedTask;
        }

        public IAsyncEnumerable<IFileEntry> ListAsync(CancellationToken cancellationToken)
            => ListCore();

        public IAsyncEnumerable<IFileEntry> ListAsync(string? path, CancellationToken cancellationToken)
            => ListCore();

        public Task<IFileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult<IFileEntry?>(null);

        public Task TestAsync(bool alsoWrite, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task CreateFolderAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<string[]> GetDNSNamesAsync(CancellationToken cancellationToken)
            => Task.FromResult(Array.Empty<string>());

        public void Dispose()
        {
            if (disposed)
                return;

            if (instanceId == TransactionInstanceId)
            {
                BackendDisposeAttempts++;
                if (BackendDisposeFailuresRemaining > 0)
                {
                    BackendDisposeFailuresRemaining--;
                    throw new InvalidOperationException("Simulated backend disposal failure.");
                }

                TransactionBackendDisposeCalls++;
            }

            disposed = true;
        }

        private static async IAsyncEnumerable<IFileEntry> EmptyEntries()
        {
            await Task.CompletedTask;
            yield break;
        }

        private IAsyncEnumerable<IFileEntry> ListCore()
        {
            if (disposed)
            {
                OperationsOnDisposedBackend++;
                throw new ObjectDisposedException(nameof(TransactionalProbeBackend));
            }

            return EmptyEntries();
        }

        private static void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref maxConcurrentOperations);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref maxConcurrentOperations, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        private sealed class ProbeTransaction(TransactionalProbeBackend backend) : IBackendTransaction
        {
            private bool committed;
            private bool rolledBack;
            private bool disposed;

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rolledBack)
                    throw new InvalidOperationException("The transaction was rolled back.");
                if (committed)
                    return Task.CompletedTask;

                CommitCalls++;
                if (FailCommit)
                    throw new InvalidOperationException("Simulated commit failure.");

                committed = true;
                backend.transactionActive = false;
                return Task.CompletedTask;
            }

            public Task RollbackAsync(Exception? exception, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (committed)
                    throw new InvalidOperationException("The transaction was committed.");
                if (rolledBack)
                    return Task.CompletedTask;

                RollbackCalls++;
                RollbackException = exception;
                if (RollbackFailuresRemaining > 0)
                {
                    RollbackFailuresRemaining--;
                    throw new InvalidOperationException("Simulated rollback failure.");
                }
                if (FailRollback)
                    throw new InvalidOperationException("Simulated rollback failure.");

                rolledBack = true;
                backend.transactionActive = false;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                if (disposed)
                    return;

                TransactionDisposeCalls++;
                if (TransactionDisposeFailuresRemaining > 0)
                {
                    TransactionDisposeFailuresRemaining--;
                    throw new InvalidOperationException("Simulated transaction disposal failure.");
                }
                if (FailTransactionDispose)
                    throw new InvalidOperationException("Simulated transaction disposal failure.");

                disposed = true;
                if (!committed && !rolledBack)
                    await RollbackAsync(null, CancellationToken.None);
            }
        }
    }
}


[TestFixture]
public sealed class ReusableTransactionTests
{
    [Test]
    public async Task DeferredCommitsRemainInvisibleUntilPublished()
    {
        var path = Path.Combine(Path.GetTempPath(), $"duplicati-deferred-transaction-{Guid.NewGuid():N}.sqlite");

        try
        {
            await using var writer = new SqliteConnection($"Data Source={path};Pooling=false");
            await writer.OpenAsync();

            await using (var setup = writer.CreateCommand())
            {
                setup.CommandText = "CREATE TABLE DeferredValue (Value INTEGER NOT NULL)";
                await setup.ExecuteNonQueryAsync();
            }

            await using var transaction = new ReusableTransaction(writer);
            transaction.DeferCommits();

            await using (var insert = writer.CreateCommand())
            {
                insert.Transaction = transaction.Transaction;
                insert.CommandText = "INSERT INTO DeferredValue (Value) VALUES (1)";
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync("IntermediateCommit");

            await using var observer = new SqliteConnection($"Data Source={path};Pooling=false");
            await observer.OpenAsync();
            await using var count = observer.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM DeferredValue";

            Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync()), Is.Zero);

            await transaction.CommitDeferredAsync("PublishDeferredCommit");

            Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync()), Is.EqualTo(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SuccessfulDeferredCommitWithFailedCleanupRemainsPublishedAndRetriesDisposal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"duplicati-deferred-cleanup-{Guid.NewGuid():N}.sqlite");

        try
        {
            await using var writer = new SqliteConnection($"Data Source={path};Pooling=false");
            await writer.OpenAsync();

            await using (var setup = writer.CreateCommand())
            {
                setup.CommandText = "CREATE TABLE DeferredValue (Value INTEGER NOT NULL)";
                await setup.ExecuteNonQueryAsync();
            }

            var disposeCalls = 0;
            ValueTask DisposeWithOneFailure(SqliteTransaction value)
            {
                disposeCalls++;
                return disposeCalls == 1
                    ? ValueTask.FromException(new InvalidOperationException("Simulated committed-transaction cleanup failure."))
                    : value.DisposeAsync();
            }

            await using var transaction = new ReusableTransaction(writer, null, DisposeWithOneFailure);
            transaction.DeferCommits();

            await using (var insert = writer.CreateCommand())
            {
                insert.Transaction = transaction.Transaction;
                insert.CommandText = "INSERT INTO DeferredValue (Value) VALUES (1)";
                await insert.ExecuteNonQueryAsync();
            }

            Assert.DoesNotThrowAsync(
                async () => await transaction.CommitDeferredAsync("PublishWithCleanupFailure"));

            await using var observer = new SqliteConnection($"Data Source={path};Pooling=false");
            await observer.OpenAsync();
            await using var count = observer.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM DeferredValue";
            Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync()), Is.EqualTo(1));

            await transaction.DisposeAsync();
            Assert.That(disposeCalls, Is.EqualTo(3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task FailedDeferredCommitKeepsOrdinaryCommitsSuppressedAndDisposalRollsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"duplicati-failed-deferred-transaction-{Guid.NewGuid():N}.sqlite");

        try
        {
            await using var writer = new SqliteConnection($"Data Source={path};Pooling=false");
            await writer.OpenAsync();

            await using (var setup = writer.CreateCommand())
            {
                setup.CommandText = @"PRAGMA foreign_keys = ON;
                    CREATE TABLE Parent (Id INTEGER PRIMARY KEY);
                    CREATE TABLE Child (
                        ParentId INTEGER NOT NULL,
                        FOREIGN KEY (ParentId) REFERENCES Parent(Id) DEFERRABLE INITIALLY DEFERRED
                    );";
                await setup.ExecuteNonQueryAsync();
            }

            await using var transaction = new ReusableTransaction(writer);
            transaction.DeferCommits();

            await using (var insert = writer.CreateCommand())
            {
                insert.Transaction = transaction.Transaction;
                insert.CommandText = "INSERT INTO Child (ParentId) VALUES (42)";
                await insert.ExecuteNonQueryAsync();
            }

            Assert.That(
                async () => await transaction.CommitDeferredAsync("InjectedDeferredForeignKeyFailure"),
                Throws.InstanceOf<SqliteException>());

            Assert.DoesNotThrowAsync(
                async () => await transaction.CommitAsync("MustRemainDeferredAfterFailedPublication"));

            await transaction.DisposeAsync();

            await using var observer = new SqliteConnection($"Data Source={path};Pooling=false");
            await observer.OpenAsync();
            await using var count = observer.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Child";
            Assert.That(Convert.ToInt64(await count.ExecuteScalarAsync()), Is.Zero);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

[TestFixture]
[NonParallelizable]
public sealed class BackupTransactionIntegrationTests : BasicSetupHelper
{
    [OneTimeSetUp]
    public void RegisterBackend()
        => BackendLoader.AddBackend(new TransactionalFileBackend());

    [SetUp]
    public void ResetBackend()
    {
        TransactionalFileBackend.Reset();
        TransactionalFileBackend.TargetFolder = TARGETFOLDER;
        File.WriteAllText(Path.Combine(DATAFOLDER, "transaction-test.txt"), "transactional backup content");
    }

    [Test]
    public void LocalCommitFailureAfterRemotePublicationRequiresDatabaseRecreation()
    {
        var calls = new List<string>();
        var localFailure = new SqliteException("Injected local commit failure", 19);

        var failure = Assert.ThrowsAsync<BackendCommittedLocalDatabaseCommitException>(
            async () => await BackupHandler.CommitBackendThenLocalAsync(
                () =>
                {
                    calls.Add("remote");
                    return Task.CompletedTask;
                },
                () =>
                {
                    calls.Add("local");
                    return Task.FromException(localFailure);
                }));

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(new[] { "remote", "local" }));
            Assert.That(failure!.InnerException, Is.SameAs(localFailure));
            Assert.That(failure.HelpID, Is.EqualTo("BackendCommittedLocalDatabaseCommitFailed"));
            Assert.That(failure.Message, Does.Contain("remote storage is authoritative").IgnoreCase);
            Assert.That(failure.Message, Does.Contain("recreate the local database").IgnoreCase);
        });
    }

    [Test]
    public async Task SuccessfulBackupCommitsAfterAllUploadsComplete()
    {
        using var controller = new Controller(
            $"{TransactionalFileBackend.Key}://target",
            CreateOptions(),
            null);

        var result = await controller.BackupAsync([DATAFOLDER]);

        await using var connection = new SqliteConnection($"Data Source={DBFILE};Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Fileset";
        var durableFilesets = Convert.ToInt64(await command.ExecuteScalarAsync());

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(durableFilesets, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.BeginCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.CommitCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.RollbackCalls, Is.Zero);
            Assert.That(TransactionalFileBackend.ActiveOperationsObservedAtCommit, Is.Zero);
            Assert.That(TransactionalFileBackend.Context, Is.Not.Null);
            Assert.That(TransactionalFileBackend.Context!.OperationId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(TransactionalFileBackend.Context.Mode, Is.EqualTo(BackendTransactionMode.ReadWrite));
            Assert.That(TransactionalFileBackend.Context.StartedAtUtc.Offset, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public async Task RemoteCommitCleanupFailureStillPublishesTheLocalFileset()
    {
        TransactionalFileBackend.TransactionDisposeFailuresRemaining = 1;
        using var controller = new Controller(
            $"{TransactionalFileBackend.Key}://target",
            CreateOptions(),
            null);

        var result = await controller.BackupAsync([DATAFOLDER]);

        await using var connection = new SqliteConnection($"Data Source={DBFILE};Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Fileset";
        var durableFilesets = Convert.ToInt64(await command.ExecuteScalarAsync());
        var visibleRemoteFiles = Directory.EnumerateFiles(TARGETFOLDER, "*", SearchOption.AllDirectories).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(durableFilesets, Is.EqualTo(1));
            Assert.That(visibleRemoteFiles, Is.Not.Empty);
            Assert.That(TransactionalFileBackend.CommitCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.RollbackCalls, Is.Zero);
            Assert.That(TransactionalFileBackend.TransactionDisposeCalls, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task FailedUploadRollsBackWithoutCommit()
    {
        TransactionalFileBackend.FailPut = true;
        IBackupResults? result = null;
        Exception? failure = null;

        using var controller = new Controller(
            $"{TransactionalFileBackend.Key}://target",
            CreateOptions(),
            null);

        try
        {
            result = await controller.BackupAsync([DATAFOLDER]);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await using var connection = new SqliteConnection($"Data Source={DBFILE};Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Fileset";
        var durableFilesets = Convert.ToInt64(await command.ExecuteScalarAsync());

        Assert.Multiple(() =>
        {
            Assert.That(failure != null || result?.Errors.Any() == true, Is.True, "The injected upload failure was not surfaced.");
            Assert.That(TransactionalFileBackend.BeginCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.CommitCalls, Is.Zero);
            Assert.That(TransactionalFileBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.TransactionDisposeCalls, Is.EqualTo(1));
            Assert.That(durableFilesets, Is.Zero, "A failed transactional upload must not leave an unpublished local fileset.");
        });
    }

    [Test]
    public async Task FailedCommitRollsBackTheStillActiveTransaction()
    {
        TransactionalFileBackend.FailCommit = true;
        IBackupResults? result = null;
        Exception? failure = null;

        using var controller = new Controller(
            $"{TransactionalFileBackend.Key}://target",
            CreateOptions(),
            null);

        try
        {
            result = await controller.BackupAsync([DATAFOLDER]);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await using var connection = new SqliteConnection($"Data Source={DBFILE};Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Fileset";
        var durableFilesets = Convert.ToInt64(await command.ExecuteScalarAsync());
        var visibleRemoteFiles = Directory.EnumerateFiles(TARGETFOLDER, "*", SearchOption.AllDirectories).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(failure != null || result?.Errors.Any() == true, Is.True, "The injected commit failure was not surfaced.");
            Assert.That(TransactionalFileBackend.BeginCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.CommitCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.TransactionDisposeCalls, Is.EqualTo(1));
            Assert.That(durableFilesets, Is.Zero, "A failed backend commit must not leave an unpublished local fileset.");
            Assert.That(visibleRemoteFiles, Is.Empty, "A failed backend commit must not publish staged remote files.");
        });
    }

    private Dictionary<string, string> CreateOptions()
    {
        var options = new Dictionary<string, string>(TestOptions)
        {
            ["no-encryption"] = "true",
            ["no-backend-verification"] = "true",
            ["number-of-retries"] = "0",
            ["asynchronous-upload-limit"] = "4",
        };
        return options;
    }

    private sealed class TransactionalFileBackend : ITransactionalBackend, IStreamingBackend, IFolderEnabledBackend
    {
        public const string Key = "transaction-file";
        private readonly IStreamingBackend? backend;
        private readonly IFolderEnabledBackend? folderBackend;
        private readonly Dictionary<string, string>? backendOptions;
        private IStreamingBackend? transactionBackend;
        private IFolderEnabledBackend? transactionFolderBackend;
        private string? transactionFolder;
        private bool transactionActive;
        private bool disposed;
        private static int activeOperations;

        public TransactionalFileBackend()
        {
        }

        public TransactionalFileBackend(string url, Dictionary<string, string> options)
        {
            backendOptions = new Dictionary<string, string>(options);
            backend = CreateFileBackend(TargetFolder);
            folderBackend = (IFolderEnabledBackend)backend;
        }

        public static string TargetFolder { get; set; } = string.Empty;
        public static bool FailPut { get; set; }
        public static bool FailCommit { get; set; }
        public static int TransactionDisposeFailuresRemaining { get; set; }
        public static int BeginCalls { get; private set; }
        public static int CommitCalls { get; private set; }
        public static int RollbackCalls { get; private set; }
        public static int TransactionDisposeCalls { get; private set; }
        public static int ActiveOperationsObservedAtCommit { get; private set; }
        public static BackendTransactionContext? Context { get; private set; }

        public string DisplayName => "Transactional file backend";
        public string ProtocolKey => Key;
        public string Description => "File backend wrapper used to verify backup transaction orchestration";
        public IList<ICommandLineArgument> SupportedCommands
            => backend?.SupportedCommands ?? BackendLoader.GetSupportedCommands("file://").ToList();
        public bool SupportsStreaming => true;

        public static void Reset()
        {
            FailPut = false;
            FailCommit = false;
            TransactionDisposeFailuresRemaining = 0;
            BeginCalls = 0;
            CommitCalls = 0;
            RollbackCalls = 0;
            TransactionDisposeCalls = 0;
            ActiveOperationsObservedAtCommit = -1;
            Context = null;
            activeOperations = 0;
        }

        public Task<IBackendTransaction> BeginTransactionAsync(BackendTransactionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (transactionActive)
                throw new InvalidOperationException("A transaction is already active.");

            var options = backendOptions
                ?? throw new InvalidOperationException("The metadata-only backend instance cannot begin a transaction.");
            var parent = Directory.GetParent(Path.GetFullPath(TargetFolder))?.FullName
                ?? throw new InvalidOperationException("The target folder has no parent directory.");
            var stagingFolder = Path.Combine(parent, $".duplicati-transaction-{Guid.NewGuid():N}");

            Directory.CreateDirectory(stagingFolder);
            try
            {
                CopyDirectory(TargetFolder, stagingFolder);
                transactionBackend = (IStreamingBackend)BackendLoader.GetBackend("file://" + stagingFolder, options);
                transactionFolderBackend = (IFolderEnabledBackend)transactionBackend;
                transactionFolder = stagingFolder;
                transactionActive = true;
            }
            catch
            {
                transactionBackend?.Dispose();
                transactionBackend = null;
                transactionFolderBackend = null;
                if (Directory.Exists(stagingFolder))
                    Directory.Delete(stagingFolder, true);
                throw;
            }

            BeginCalls++;
            Context = context;
            return Task.FromResult<IBackendTransaction>(new Transaction(this));
        }

        public Task PutAsync(string remotename, string filename, CancellationToken cancellationToken)
            => RunPutAsync(() => RequiredBackend.PutAsync(remotename, filename, cancellationToken));

        public Task PutAsync(string remotename, Stream stream, CancellationToken cancellationToken)
            => RunPutAsync(() => RequiredBackend.PutAsync(remotename, stream, cancellationToken));

        private async Task RunPutAsync(Func<Task> action)
        {
            if (!transactionActive)
                throw new InvalidOperationException("Upload was attempted outside the active transaction.");

            Interlocked.Increment(ref activeOperations);
            try
            {
                if (FailPut)
                    throw new IOException("Injected transactional upload failure.");
                await action().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref activeOperations);
            }
        }

        public Task GetAsync(string remotename, string filename, CancellationToken cancellationToken)
            => RequiredBackend.GetAsync(remotename, filename, cancellationToken);

        public Task GetAsync(string remotename, Stream stream, CancellationToken cancellationToken)
            => RequiredBackend.GetAsync(remotename, stream, cancellationToken);

        public Task DeleteAsync(string remotename, CancellationToken cancellationToken)
            => RequiredBackend.DeleteAsync(remotename, cancellationToken);

        public IAsyncEnumerable<IFileEntry> ListAsync(CancellationToken cancellationToken)
            => RequiredBackend.ListAsync(cancellationToken);

        public IAsyncEnumerable<IFileEntry> ListAsync(string? path, CancellationToken cancellationToken)
            => RequiredFolderBackend.ListAsync(path, cancellationToken);

        public Task<IFileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
            => RequiredFolderBackend.GetEntryAsync(path, cancellationToken);

        public Task TestAsync(bool alsoWrite, CancellationToken cancellationToken)
            => RequiredBackend.TestAsync(alsoWrite, cancellationToken);

        public Task CreateFolderAsync(CancellationToken cancellationToken)
            => RequiredBackend.CreateFolderAsync(cancellationToken);

        public Task<string[]> GetDNSNamesAsync(CancellationToken cancellationToken)
            => RequiredBackend.GetDNSNamesAsync(cancellationToken);

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            transactionBackend?.Dispose();
            transactionBackend = null;
            transactionFolderBackend = null;
            if (transactionFolder != null && Directory.Exists(transactionFolder))
                Directory.Delete(transactionFolder, true);
            transactionFolder = null;
            transactionActive = false;
            backend?.Dispose();
        }

        private IStreamingBackend RequiredBackend
            => transactionActive
                ? transactionBackend ?? throw new InvalidOperationException("The active transaction has no staging backend.")
                : backend ?? throw new InvalidOperationException("The metadata-only backend instance cannot perform operations.");

        private IFolderEnabledBackend RequiredFolderBackend
            => transactionActive
                ? transactionFolderBackend ?? throw new InvalidOperationException("The active transaction has no staging folder backend.")
                : folderBackend ?? throw new InvalidOperationException("The metadata-only backend instance cannot perform folder operations.");

        private IStreamingBackend CreateFileBackend(string folder)
            => (IStreamingBackend)BackendLoader.GetBackend(
                "file://" + folder,
                backendOptions ?? throw new InvalidOperationException("Backend options are unavailable."));

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            if (!Directory.Exists(source))
                return;

            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }

        private void PublishTransaction()
        {
            var stagingFolder = transactionFolder
                ?? throw new InvalidOperationException("The active transaction has no staging folder.");
            var previousFolder = TargetFolder + $".previous-{Guid.NewGuid():N}";

            transactionBackend?.Dispose();
            transactionBackend = null;
            transactionFolderBackend = null;

            try
            {
                if (Directory.Exists(TargetFolder))
                    Directory.Move(TargetFolder, previousFolder);
                Directory.Move(stagingFolder, TargetFolder);
            }
            catch
            {
                if (!Directory.Exists(TargetFolder) && Directory.Exists(previousFolder))
                    Directory.Move(previousFolder, TargetFolder);

                transactionBackend = CreateFileBackend(stagingFolder);
                transactionFolderBackend = (IFolderEnabledBackend)transactionBackend;
                throw;
            }

            transactionFolder = null;
            transactionActive = false;
            if (Directory.Exists(previousFolder))
            {
                try { Directory.Delete(previousFolder, true); }
                catch { /* The committed target is already published; stale test data is removed by teardown. */ }
            }
        }

        private void RollbackTransaction()
        {
            var stagingFolder = transactionFolder;
            transactionBackend?.Dispose();
            transactionBackend = null;
            transactionFolderBackend = null;
            if (stagingFolder != null && Directory.Exists(stagingFolder))
                Directory.Delete(stagingFolder, true);
            transactionFolder = null;
            transactionActive = false;
        }

        private sealed class Transaction(TransactionalFileBackend owner) : IBackendTransaction
        {
            private bool committed;
            private bool rolledBack;
            private bool disposed;

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rolledBack)
                    throw new InvalidOperationException("The transaction was rolled back.");
                if (committed)
                    return Task.CompletedTask;

                ActiveOperationsObservedAtCommit = Volatile.Read(ref activeOperations);
                CommitCalls++;
                if (FailCommit)
                    throw new InvalidOperationException("Injected transactional commit failure.");

                owner.PublishTransaction();
                committed = true;
                return Task.CompletedTask;
            }

            public Task RollbackAsync(Exception? exception, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (committed)
                    throw new InvalidOperationException("The transaction was committed.");
                if (rolledBack)
                    return Task.CompletedTask;

                RollbackCalls++;
                owner.RollbackTransaction();
                rolledBack = true;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                if (disposed)
                    return;

                TransactionDisposeCalls++;
                if (TransactionDisposeFailuresRemaining > 0)
                {
                    TransactionDisposeFailuresRemaining--;
                    throw new InvalidOperationException("Injected transaction cleanup failure.");
                }
                if (!committed && !rolledBack)
                    await RollbackAsync(null, CancellationToken.None).ConfigureAwait(false);
                disposed = true;
            }
        }
    }
}
