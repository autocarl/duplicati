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
using Duplicati.Library.Utility;
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

    private static BackendManager CreateManager(string url, BackupResults results)
        => new(
            url,
            new Options(new Dictionary<string, string?>
            {
                ["asynchronous-upload-limit"] = "4",
                ["restore-volume-downloaders"] = "4",
                ["number-of-retries"] = "0",
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
        public static int TransactionInstanceId { get; private set; }
        public static int CommitCalls { get; private set; }
        public static int RollbackCalls { get; private set; }
        public static int TransactionDisposeCalls { get; private set; }
        public static int TransactionBackendDisposeCalls { get; private set; }
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
            TransactionInstanceId = 0;
            CommitCalls = 0;
            RollbackCalls = 0;
            TransactionDisposeCalls = 0;
            TransactionBackendDisposeCalls = 0;
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
            => Task.CompletedTask;

        public IAsyncEnumerable<IFileEntry> ListAsync(CancellationToken cancellationToken)
            => EmptyEntries();

        public IAsyncEnumerable<IFileEntry> ListAsync(string? path, CancellationToken cancellationToken)
            => EmptyEntries();

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

            disposed = true;
            if (instanceId == TransactionInstanceId)
                TransactionBackendDisposeCalls++;
        }

        private static async IAsyncEnumerable<IFileEntry> EmptyEntries()
        {
            await Task.CompletedTask;
            yield break;
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
                rolledBack = true;
                backend.transactionActive = false;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                if (disposed)
                    return;

                disposed = true;
                TransactionDisposeCalls++;
                if (!committed && !rolledBack)
                    await RollbackAsync(null, CancellationToken.None);
            }
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
    public async Task SuccessfulBackupCommitsAfterAllUploadsComplete()
    {
        using var controller = new Controller(
            $"{TransactionalFileBackend.Key}://target",
            CreateOptions(),
            null);

        var result = await controller.BackupAsync([DATAFOLDER]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Is.Empty);
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

        Assert.Multiple(() =>
        {
            Assert.That(failure != null || result?.Errors.Any() == true, Is.True, "The injected upload failure was not surfaced.");
            Assert.That(TransactionalFileBackend.BeginCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.CommitCalls, Is.Zero);
            Assert.That(TransactionalFileBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.TransactionDisposeCalls, Is.EqualTo(1));
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

        Assert.Multiple(() =>
        {
            Assert.That(failure != null || result?.Errors.Any() == true, Is.True, "The injected commit failure was not surfaced.");
            Assert.That(TransactionalFileBackend.BeginCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.CommitCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.RollbackCalls, Is.EqualTo(1));
            Assert.That(TransactionalFileBackend.TransactionDisposeCalls, Is.EqualTo(1));
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
        private bool transactionActive;
        private bool disposed;
        private static int activeOperations;

        public TransactionalFileBackend()
        {
        }

        public TransactionalFileBackend(string url, Dictionary<string, string> options)
        {
            backend = (IStreamingBackend)BackendLoader.GetBackend("file://" + TargetFolder, options);
            folderBackend = (IFolderEnabledBackend)backend;
        }

        public static string TargetFolder { get; set; } = string.Empty;
        public static bool FailPut { get; set; }
        public static bool FailCommit { get; set; }
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

            BeginCalls++;
            Context = context;
            transactionActive = true;
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
            backend?.Dispose();
        }

        private IStreamingBackend RequiredBackend
            => backend ?? throw new InvalidOperationException("The metadata-only backend instance cannot perform operations.");

        private IFolderEnabledBackend RequiredFolderBackend
            => folderBackend ?? throw new InvalidOperationException("The metadata-only backend instance cannot perform folder operations.");

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

                committed = true;
                owner.transactionActive = false;
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
                rolledBack = true;
                owner.transactionActive = false;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                if (disposed)
                    return;

                disposed = true;
                TransactionDisposeCalls++;
                if (!committed && !rolledBack)
                    await RollbackAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
