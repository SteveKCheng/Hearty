using System;
using System.Text;

namespace JobBank.Server.Program
{
    public static class BasicExceptionTranslator
    {
        /// <summary>
        /// Encodes strings to UTF-8 without the so-called "byte order mark".
        /// </summary>
        private static readonly Encoding Utf8NoBOM = new UTF8Encoding(false);

        private static PromiseData Translate(PromiseId? promiseId, Exception exception)
        {
            var bytes = Utf8NoBOM.GetBytes(exception.ToString());
            return new Payload("text/x-exception; charset=utf-8", bytes);
        }

        public static PromiseExceptionTranslator Instance { get; } = Translate;
    }
}
