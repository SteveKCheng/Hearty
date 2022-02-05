using System;
using System.Text;

namespace JobBank.Server.Program
{
    public static class BasicExceptionTranslator
    {
        private static PromiseData Translate(PromiseId? promiseId, Exception exception)
            => new PromiseExceptionalData(exception);

        public static PromiseExceptionTranslator Instance { get; } = Translate;
    }
}
