using System;
using System.Diagnostics.CodeAnalysis;

namespace JobBank.Server
{
    /// <summary>
    /// A filesystem-like path to a promise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How promises are stored is analogous to how
    /// Unix filesystems work at a high level. 
    /// Each promise is a file, and 
    /// <see cref="PromiseId" /> is its "inode" number.
    /// There may be any number of named paths to the
    /// same inode, that can be added or removed at
    /// anytime even though the promise results stay
    /// immutable.  
    /// </para>
    /// <para>
    /// The paths are treated as "hard links".
    /// The same promise may be made available at multiple paths.
    /// The paths can be queried in path-comparison order, 
    /// which means they can be used to implement secondary indices
    /// (like in SQL databases) on promises (which would be analogous
    /// to rows in a database table).  A folder can name
    /// the secondary index, and sub-paths within it are the
    /// string- or integer-valued entries in the secondary index.
    /// </para>
    /// <para>
    /// The file name component is split from the folder path so 
    /// that it can be number, and also to effect a weak form of 
    /// prefix compression.  Full prefix compression is not done
    /// as most paths are expected to be fairly short, so that 
    /// replacing parts of paths with object references (which require
    /// 8 bytes on 64-bit environments) would not save much and
    /// would slow down comparisons when this type is used as a 
    /// look-up key.
    /// </para>
    /// </remarks>
    public readonly struct PromisePath : IComparable<PromisePath>
                                       , IEquatable<PromisePath>
    {
        /// <summary>
        /// An integer assigned as the "file name" of the promise
        /// within its containing folder. 
        /// </summary>
        /// <remarks>
        /// This integer is analogous to the auto-incrementing
        /// row ID in SQL databases.  If the file name is to be
        /// a string instead, this number shall be assigned as zero.
        /// </remarks>
        public uint Number { get; }

        /// <summary>
        /// The path to the folder ("directories" in the traditional
        /// terminology of filesystems).
        /// </summary>
        /// <remarks>
        /// Component names in nested folders are separated by a slash ('/').
        /// The string is compared case-sensitively.  To ensure unique
        /// representation, there may be no leading, trailing, or consecutively
        /// repeated slashes.  The root folder is represented by the empty string.
        /// </remarks>
        public string Folder { get; }

        /// <summary>
        /// The name of the file within its containing folder.
        /// </summary>
        /// <remarks>
        /// This string is null only when the file name
        /// should be an (auto-incrementing) integer: see
        /// <see cref="Number" />).  A folder is represented
        /// by its string path in <see cref="Folder" /> while
        /// this member must be set to the empty string.
        /// The name of a file cannot be blank.
        /// </remarks>
        public string? Name { get; }

        private PromisePath(uint number, string folder, string? name)
        {
            Number = number;
            Folder = folder;
            Name = name;
        }

        /// <summary>
        /// Construct the path for a folder itself.
        /// </summary>
        public static PromisePath ForFolder(string folder)
            => new PromisePath(0, folder, string.Empty);

        /// <summary>
        /// Construct the path for a named file.
        /// </summary>
        public static PromisePath ForNamedFile(string folder, string name)
            => new PromisePath(0, folder, name);

        /// <summary>
        /// Construct the path for a numbered file.
        /// </summary>
        public static PromisePath ForNumberedFile(string folder, uint number)
            => new PromisePath(number, folder, null);

        /// <inheritdoc cref="IComparable{T}.CompareTo(T?)" />
        public int CompareTo(PromisePath other)
        {
            int c = Folder.CompareTo(other.Folder);
            if (c != 0)
                return c;

            c = Number.CompareTo(other.Number);
            if (c != 0)
                return c;

            return Name!.CompareTo(other.Name);
        }

        /// <inheritdoc cref="IEquatable{T}.Equals(T?)" />
        public bool Equals(PromisePath other)
            => CompareTo(other) == 0;

        /// <inheritdoc cref="object.Equals(object?)" />
        public override bool Equals([NotNullWhen(true)] object? obj)
            => obj is PromisePath other && Equals(other);

        /// <inheritdoc cref="object.GetHashCode" />
        public override int GetHashCode()
        {
            return HashCode.Combine(Number.GetHashCode(),
                                    Folder.GetHashCode(),
                                    Name?.GetHashCode() ?? 0);
        }

        public static bool operator ==(PromisePath left, PromisePath right)
            => left.Equals(right);

        public static bool operator !=(PromisePath left, PromisePath right)
            => !(left == right);

        public static bool operator <(PromisePath left, PromisePath right)
            => left.CompareTo(right) < 0;

        public static bool operator <=(PromisePath left, PromisePath right)
            => left.CompareTo(right) <= 0;

        public static bool operator >(PromisePath left, PromisePath right)
            => left.CompareTo(right) > 0;

        public static bool operator >=(PromisePath left, PromisePath right)
            => left.CompareTo(right) >= 0;
    }
}
