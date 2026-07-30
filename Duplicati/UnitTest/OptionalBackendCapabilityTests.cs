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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Duplicati.Library.Interface;
using NUnit.Framework;

namespace Duplicati.UnitTest;

[TestFixture]
public sealed class OptionalBackendCapabilityTests
{
    [Test]
    public void TransactionContextHasStableVersionedDefaults()
    {
        var before = DateTimeOffset.UtcNow;
        var context = new BackendTransactionContext
        {
            OperationId = Guid.NewGuid(),
            OperationName = "Backup",
            Mode = BackendTransactionMode.ReadWrite,
        };
        var after = DateTimeOffset.UtcNow;

        Assert.Multiple(() =>
        {
            Assert.That(context.ContractVersion, Is.EqualTo(1));
            Assert.That(context.StartedAtUtc, Is.InRange(before, after));
            Assert.That(context.StartedAtUtc.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(context.Extensions, Is.Not.Null.And.Empty);
            Assert.That((int)BackendTransactionMode.ReadOnly, Is.Zero);
            Assert.That((int)BackendTransactionMode.ReadWrite, Is.EqualTo(1));
        });
    }

    [Test]
    public void TransactionContextRejectsInvalidMetadata()
    {
        var operationId = Guid.NewGuid();
        var nonUtcTimestamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1));

        Assert.Multiple(() =>
        {
            Assert.That(
                () => new BackendTransactionContext
                {
                    OperationId = Guid.Empty,
                    OperationName = "Backup",
                    Mode = BackendTransactionMode.ReadWrite,
                },
                Throws.TypeOf<ArgumentException>());
            Assert.That(
                () => new BackendTransactionContext
                {
                    OperationId = operationId,
                    OperationName = "   ",
                    Mode = BackendTransactionMode.ReadWrite,
                },
                Throws.TypeOf<ArgumentException>());
            Assert.That(
                () => new BackendTransactionContext
                {
                    ContractVersion = 0,
                    OperationId = operationId,
                    OperationName = "Backup",
                    Mode = BackendTransactionMode.ReadWrite,
                },
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new BackendTransactionContext
                {
                    OperationId = operationId,
                    OperationName = "Backup",
                    Mode = (BackendTransactionMode)99,
                },
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new BackendTransactionContext
                {
                    OperationId = operationId,
                    OperationName = "Backup",
                    Mode = BackendTransactionMode.ReadWrite,
                    StartedAtUtc = default
                },
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new BackendTransactionContext
                {
                    OperationId = operationId,
                    OperationName = "Backup",
                    Mode = BackendTransactionMode.ReadWrite,
                    StartedAtUtc = nonUtcTimestamp
                },
                Throws.TypeOf<ArgumentException>());
            Assert.That(
                () => new BackendTransactionContext
                {
                    OperationId = operationId,
                    OperationName = "Backup",
                    Mode = BackendTransactionMode.ReadWrite,
                    Extensions = null!
                },
                Throws.TypeOf<ArgumentNullException>());
        });
    }

    [Test]
    public void TransactionContextDefensivelyCopiesExtensions()
    {
        var source = new Dictionary<string, string>
        {
            ["pbs.namespace"] = "initial"
        };
        var context = new BackendTransactionContext
        {
            OperationId = Guid.NewGuid(),
            OperationName = "Backup",
            Mode = BackendTransactionMode.ReadWrite,
            Extensions = source
        };

        source["pbs.namespace"] = "changed";
        source["unexpected"] = "added";

        Assert.Multiple(() =>
        {
            Assert.That(context.Extensions, Is.Not.SameAs(source));
            Assert.That(context.Extensions, Has.Count.EqualTo(1));
            Assert.That(context.Extensions["pbs.namespace"], Is.EqualTo("initial"));
        });
    }

    [Test]
    public void TransactionCapabilityDoesNotChangeIBackend()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(ITransactionalBackend).GetInterfaces(), Does.Contain(typeof(IBackend)));
            Assert.That(typeof(IBackend).GetInterfaces(), Does.Not.Contain(typeof(ITransactionalBackend)));
            Assert.That(
                typeof(IBackend).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(method => method.Name),
                Does.Not.Contain(nameof(ITransactionalBackend.BeginTransactionAsync)));
        });
    }

    [Test]
    public void IBackendPublicApiMatchesCompatibilityBaseline()
    {
        var declaredMethods = typeof(IBackend)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(FormatMethodSignature);

        Assert.Multiple(() =>
        {
            Assert.That(
                typeof(IBackend).GetInterfaces(),
                Is.EquivalentTo(new[] { typeof(IDynamicModule), typeof(IDisposable) }));
            Assert.That(
                declaredMethods,
                Is.EquivalentTo(new[]
                {
                    "System.String get_DisplayName()",
                    "System.String get_ProtocolKey()",
                    "System.Collections.Generic.IAsyncEnumerable<Duplicati.Library.Interface.IFileEntry> ListAsync(System.Threading.CancellationToken)",
                    "System.Threading.Tasks.Task PutAsync(System.String,System.String,System.Threading.CancellationToken)",
                    "System.Threading.Tasks.Task GetAsync(System.String,System.String,System.Threading.CancellationToken)",
                    "System.Threading.Tasks.Task DeleteAsync(System.String,System.Threading.CancellationToken)",
                    "System.String get_Description()",
                    "System.Threading.Tasks.Task<System.String[]> GetDNSNamesAsync(System.Threading.CancellationToken)",
                    "System.Threading.Tasks.Task TestAsync(System.Boolean,System.Threading.CancellationToken)",
                    "System.Threading.Tasks.Task CreateFolderAsync(System.Threading.CancellationToken)"
                }));
        });
    }

    [Test]
    public void ExistingBackendDoesNotNeedTheOptionalCapability()
    {
        Assert.That(
            typeof(FolderExistsBackend).GetInterfaces(),
            Does.Not.Contain(typeof(ITransactionalBackend)));
    }

    [Test]
    public void CriticalTransactionMethodsHaveNoDefaultImplementation()
    {
        var begin = typeof(ITransactionalBackend).GetMethod(nameof(ITransactionalBackend.BeginTransactionAsync));
        var commit = typeof(IBackendTransaction).GetMethod(nameof(IBackendTransaction.CommitAsync));
        var rollback = typeof(IBackendTransaction).GetMethod(nameof(IBackendTransaction.RollbackAsync));

        Assert.Multiple(() =>
        {
            Assert.That(begin, Is.Not.Null);
            Assert.That(begin!.IsAbstract, Is.True);
            Assert.That(commit, Is.Not.Null);
            Assert.That(commit!.IsAbstract, Is.True);
            Assert.That(rollback, Is.Not.Null);
            Assert.That(rollback!.IsAbstract, Is.True);
        });
    }

    private static string FormatMethodSignature(MethodInfo method)
        => $"{FormatTypeName(method.ReturnType)} {method.Name}({string.Join(",", method.GetParameters().Select(parameter => FormatTypeName(parameter.ParameterType)))})";

    private static string FormatTypeName(Type type)
    {
        if (type.IsArray)
            return $"{FormatTypeName(type.GetElementType()!)}[]";

        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var genericTypeName = type.GetGenericTypeDefinition().FullName!;
        genericTypeName = genericTypeName[..genericTypeName.IndexOf('`')];
        return $"{genericTypeName}<{string.Join(",", type.GetGenericArguments().Select(FormatTypeName))}>";
    }
}
