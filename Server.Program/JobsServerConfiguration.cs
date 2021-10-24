using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JobBank.Server
{
    /// <summary>
    /// Groups the dependencies and settings needed to service reqeusts under
    /// the Job Bank framework.
    /// </summary>
    public class JobsServerConfiguration
    {
        /// <summary>
        /// The implementation of <see cref="PromiseStorage" />.
        /// </summary>
        public PromiseStorage PromiseStorage { get; }

        /// <summary>
        /// The implementation of <see cref="PathsDirectory" />.
        /// </summary>
        public PathsDirectory PathsDirectory { get; }

        /// <summary>
        /// Establish the required components for the Job Bank framework.
        /// </summary>
        /// <remarks>
        /// This constructor can be invoked by dependency injection
        /// for each component.
        /// </remarks>
        public JobsServerConfiguration(PromiseStorage promiseStorage, 
                                      PathsDirectory pathsDirectory)
        {
            PromiseStorage = promiseStorage ?? throw new ArgumentNullException(nameof(promiseStorage));
            PathsDirectory = pathsDirectory ?? throw new ArgumentNullException(nameof(pathsDirectory));
        }
    }
}
