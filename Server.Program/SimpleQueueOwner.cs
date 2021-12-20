namespace JobBank.Server.Program
{
    internal class SimpleQueueOwner : IJobQueueOwner
    {
        public string Title { get; }

        public SimpleQueueOwner(string title) => Title = title;

        public bool Equals(IJobQueueOwner? other)
            => object.ReferenceEquals(this, other);

        public override string ToString() => Title;
    }
}
