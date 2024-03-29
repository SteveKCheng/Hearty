﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

namespace Hearty.Carp;

/// <summary>
/// An asynchronous function that can be invoked by the RPC
/// framework of this library.
/// </summary>
/// <typeparam name="TRequest">User-defined type for the request inputs. </typeparam>
/// <typeparam name="TReply">User-defined type for the reply outputs. </typeparam>
/// <param name="request">De-serialized request inputs.
/// </param>
/// <param name="connection">
/// The RPC connection that caused this function to be invoked.
/// </param>
/// <param name="cancellationToken">
/// Cancellation token that can be from the RPC client. 
/// </param>
/// <returns>
/// Asynchronous task that completes with the results to 
/// serialize and send back to the RPC client.
/// </returns>
public delegate ValueTask<TReply> RpcFunction<TRequest, TReply>(
    TRequest request, 
    RpcConnection connection,
    CancellationToken cancellationToken);

/// <summary>
/// Collects callbacks to process incoming remote procedure calls
/// and directs the serialization of payloads.
/// </summary>
public class RpcRegistry
{
    private readonly Dictionary<ushort, RpcMessageProcessor> _entries
        = new();

    private bool _isFrozen;

    internal readonly IRpcExceptionSerializer _exceptionSerializer;

    /// <summary>
    /// Construct with user-specified settings for 
    /// MessagePack payloads, and an exception serializer.
    /// </summary>
    /// <param name="exceptionSerializer">
    /// Invoked to serialize exceptions when a procedure call
    /// requested by a remote side fails, and to de-serialize 
    /// failure replies from procedure calls made to a remote side.
    /// </param>
    /// <param name="serializeOptions">
    /// Settings for serializing and de-serializing .NET types
    /// as MessagePack payloads.
    /// </param>
    public RpcRegistry(IRpcExceptionSerializer exceptionSerializer, 
                       MessagePackSerializerOptions serializeOptions)
    {
        _serializeOptions = serializeOptions 
            ?? throw new ArgumentNullException(nameof(serializeOptions));
        _exceptionSerializer = exceptionSerializer;
    }

    /// <summary>
    /// Construct with standard MessagePack serialization 
    /// settings that defend against untrusted paylods.
    /// </summary>
    public RpcRegistry(IRpcExceptionSerializer exceptionSerializer)
        : this(exceptionSerializer, StandardSerializeOptions)
    {
    }

    /// <summary>
    /// Standard MessagePack serialization 
    /// settings that defend against untrusted paylods
    /// but with no other customizations.
    /// </summary>
    public static MessagePackSerializerOptions StandardSerializeOptions { get; }
        = MessagePackSerializerOptions.Standard
                                      .WithSecurity(MessagePackSecurity.UntrustedData);

    private MessagePackSerializerOptions _serializeOptions;

    /// <summary>
    /// Settings for serializing and de-serializing .NET types
    /// as MessagePack payloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property may be changed until the RPC registry is "frozen"
    /// when a connection is established using it.  
    /// </para>
    /// <para>
    /// Fortunately, as <see cref="MessagePackSerializerOptions" />
    /// is an immutable object, no locks need to be taken when using
    /// it even if this property may be written to.
    /// </para>
    /// </remarks>
    public MessagePackSerializerOptions SerializeOptions
    {
        get => _serializeOptions;
        set
        {
            ThrowIfFrozen();
            lock (_entries)
            {
                ThrowIfFrozen();
                _serializeOptions = value ?? throw new ArgumentNullException(nameof(value));
            }
        }
    }

    /// <summary>
    /// Whether the registry has been frozen and no more entries
    /// may be added to it.
    /// </summary>
    /// <remarks>
    /// Freezing the registry allows it to be consulted without
    /// taking any locks.
    /// </remarks>
    public bool IsFrozen => _isFrozen;

    private void ThrowIfFrozen()
    {
        if (_isFrozen)
            throw new InvalidOperationException("A frozen RpcRequestRegistry instance cannot have more entries added to it. ");
    }

    /// <summary>
    /// Register an asynchronous function that processes a specific
    /// type of request and emits its reply.
    /// </summary>
    /// <typeparam name="TRequest">User-defined type for the request inputs. </typeparam>
    /// <typeparam name="TReply">User-defined type for the reply outputs. </typeparam>
    /// <param name="typeCode">Integer code that identifies the type of request
    /// over the network connection. </param>
    /// <param name="func">
    /// The asynchronous function that processes a request of the specified
    /// type and emits its reply.
    /// </param>
    /// <remarks>
    /// Registrations are no longer allowed after the RPC registry is "frozen",
    /// when a connection is established using it.  
    /// </remarks>
    public void Add<TRequest, TReply>(ushort typeCode, 
                                      RpcFunction<TRequest, TReply> func)
    {
        ThrowIfFrozen();

        lock (_entries)
        {
            ThrowIfFrozen();
            _entries.Add(typeCode, new RpcRequestProcessor<TRequest, TReply>(func));
        }
    }

    /// <summary>
    /// Get a reference to the mapping of callbacks that is guaranteed
    /// to be immutable.
    /// </summary>
    internal Dictionary<ushort, RpcMessageProcessor> Capture()
    {
        Freeze();
        return _entries;
    }

    /// <summary>
    /// Freeze the state of this object, preventing any more entries
    /// from being added.
    /// </summary>
    public void Freeze()
    {
        if (_isFrozen)
            return;

        lock (_entries)
        {
            _isFrozen = true;
        }
    }
}
