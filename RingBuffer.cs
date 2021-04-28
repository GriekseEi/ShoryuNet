namespace ShoryuNet
{
    /// <summary>
    /// A RingBuffer that can only hold a fixed amount of elements, and on full capacity will push out the oldest element if a new one is added
    /// </summary>
    /// <typeparam name="TElement">The type of the object that is stored in the RingBuffer</typeparam>
    public struct RingBuffer<TElement>
    {
        private readonly TElement[] _elements;
        private int _lastSetIndex;

        public int Length => _elements.Length;

        /// <summary>
        /// Initializes the RingBuffer
        /// </summary>
        /// <param name="capacity">The amount of objects a RingBuffer can store</param>
        public RingBuffer(int capacity)
        {
            _elements = new TElement[capacity];
            _lastSetIndex = -1;
        }

        private int LoopedIndex(int index)
        {
            return (index < 0 ? _elements.Length - index : index) % _elements.Length;
        }

        public TElement this[int index]
        {
            get => _elements[LoopedIndex(index)];
            set
            {
                _lastSetIndex = LoopedIndex(index);
                _elements[_lastSetIndex] = value;
            }
        }
    }
}
