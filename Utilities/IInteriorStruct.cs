using System;

namespace JobBank.Utilities
{
    /// <summary>
    /// Enables access to a structure embedded in a larger class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface helps to implement intrusive (mutable) data structures,
    /// or common functionality embodied in a (mutable) .NET structure that
    /// requires taking a reference to itself.  
    /// </para>
    /// <para>
    /// Of course, references to structures cannot be stored inside .NET types.
    /// But within a .NET method, local variables may be interior references,
    /// which can be materialized if we have an object reference to the containing
    /// class instance, and this accessor which then derives the interior reference.
    /// </para>
    /// <para>
    /// This interface is meant to be implemented by dummy structures that are 
    /// private to the containing class <typeparamref name="T"/>.  The dummy
    /// structure should contain no data members.  Calls to the sole interface 
    /// method here then will end up as direct and inlinable
    /// after monomorphization.  (Do not implement this interface using 
    /// a class, as that would introduce interface dispatch overheads.)
    /// It has been verified by disassembly that RyuJIT optimizes the
    /// retrieval and use of the interior references such that there is no difference
    /// versus accessing the structure by direct field access 
    /// (which cannot be made generic).
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The class whose instances contain the desired 
    /// instance of <typeparamref name="S"/>. 
    /// </typeparam>
    /// <typeparam name="S">The structure whose instances need to be retrieved
    /// by interior reference.
    /// </typeparam>
    public interface IInteriorStruct<T, S> 
        where T : class 
        where S : struct
    {
        /// <summary>
        /// Retrieve the interior reference to some designated 
        /// instance of <typeparamref name="S"/> within <typeparamref name="T" />.
        /// </summary>
        /// <param name="parent">Reference to an object that holds the 
        /// desired structure.
        /// </param>
        /// <remarks>
        /// This interface method should be treated as static,
        /// because the callers cannot be relied to persist any 
        /// instances of <see cref="IInteriorStruct{T, S}"/>.  
        /// They, instead, default-construct the implementation
        /// (assumed to be a structure) whenever they need to
        /// call this method.  Currently, .NET cannot 
        /// express the concept of a "static interface method" 
        /// but it is likely to be able to in the future.
        /// </remarks>
        /// <returns>
        /// Interior reference to the structure within <paramref name="parent" />.
        /// </returns>
        ref S GetInteriorReference(T parent);
    }
}
