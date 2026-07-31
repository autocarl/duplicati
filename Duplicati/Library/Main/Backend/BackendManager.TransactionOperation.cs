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
using System.Threading;
using System.Threading.Tasks;
using Duplicati.Library.Interface;

namespace Duplicati.Library.Main.Backend;

partial class BackendManager
{
    /// <summary>
    /// Base class for transaction control messages that are interpreted by the
    /// backend handler rather than executed as ordinary backend operations.
    /// </summary>
    private abstract class TransactionControlOperation<TResult>(
        ExecuteContext context,
        CancellationToken cancellationToken)
        : PendingOperation<TResult>(context, true, cancellationToken)
    {
        public sealed override Task<TResult> ExecuteAsync(IBackend backend, CancellationToken cancelToken)
            => throw new NotSupportedException("Transaction control operations must be handled by the backend manager.");
    }

    private sealed class BeginTransactionOperation(
        BackendTransactionContext transactionContext,
        ExecuteContext context,
        CancellationToken cancellationToken)
        : TransactionControlOperation<bool>(context, cancellationToken)
    {
        public BackendTransactionContext TransactionContext { get; } = transactionContext;
        public override BackendActionType Operation => BackendActionType.TransactionBegin;
    }

    private sealed class CommitTransactionOperation(
        ExecuteContext context,
        CancellationToken cancellationToken)
        : TransactionControlOperation<bool>(context, cancellationToken)
    {
        public override BackendActionType Operation => BackendActionType.TransactionCommit;
    }

    private sealed class RollbackTransactionOperation(
        Exception? exception,
        ExecuteContext context,
        CancellationToken cancellationToken)
        : TransactionControlOperation<bool>(context, cancellationToken)
    {
        public Exception? Exception { get; } = exception;
        public override BackendActionType Operation => BackendActionType.TransactionRollback;
    }
}
