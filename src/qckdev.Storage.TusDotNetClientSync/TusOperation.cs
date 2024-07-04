using System;
using System.Runtime.CompilerServices;

namespace qckdev.Storage.TusDotNetClientSync
{
    /// <summary>
    /// A delegate used for reporting progress of a transfer of bytes.
    /// </summary>
    /// <param name="bytesTransferred">The number of bytes transferred so far.</param>
    /// <param name="bytesTotal">The total number of bytes to transfer.</param>
    public delegate void ProgressDelegate(long bytesTransferred, long bytesTotal);

    /// <summary>
    /// Represents an operation against a Tus enabled server. <see cref="TusOperation{T}"/> supports progress reports.
    /// </summary>
    /// <typeparam name="T">The type of the operation result.</typeparam>
    public class TusOperation<T>
    {
        private readonly OperationDelegate _operation;
        private T _operationResult;

        /// <summary>
        /// Represents an operation which receives a delegate to report transfer progress to.
        /// </summary>
        /// <param name="reportProgress">A delegate which transfer progress can be reported to.</param>
        public delegate T OperationDelegate(ProgressDelegate reportProgress);

        /// <summary>
        /// Occurs when progress sending the request is made.
        /// </summary>
        public event ProgressDelegate Progressed;

        /// <summary>
        /// Get the asynchronous operation to be performed. This will initiate the operation.
        /// </summary>
        public T Operation
        {
            get
            {
                if (_operationResult == null)
                {
                    _operationResult = _operation((transferred, total) => 
                        Progressed?.Invoke(transferred, total));
                }
                return _operationResult;
            }
        }

        /// <summary>
        /// Create an instance of a <see cref="TusOperation{T}"/>
        /// </summary>
        /// <param name="operation">The operation to perform.</param>
        internal TusOperation(OperationDelegate operation)
        {
            _operation = operation;
        }

        /// <summary>
        /// Gets the operation.
        /// </summary>
        /// <returns>The operation.</returns>
        public T Get() => Operation;

    }
}