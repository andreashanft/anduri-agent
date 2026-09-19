using Anduri.Agent.Protocol;

namespace Anduri.Agent.Sensors;

/// <summary>
/// The last 15 minutes of every sensor at 1-second resolution, in a ring of per-second slots shared by all sensors.
/// </summary>
public sealed class HistoryBuffer
{
    public const int DefaultCapacitySeconds = 15 * 60;

    private readonly Lock gate = new();
    private readonly int capacity;
    // The Unix second each slot currently holds; a slot is reused once its second falls out of the window.
    private readonly long[] slotSeconds;
    // NaN marks "no sample in this second".
    private readonly Dictionary<string, double[]> series = new(StringComparer.Ordinal);
    private long latestSecond = long.MinValue;

    public HistoryBuffer(int capacitySeconds = DefaultCapacitySeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacitySeconds, 1);
        capacity = capacitySeconds;
        slotSeconds = new long[capacitySeconds];
        Array.Fill(slotSeconds, long.MinValue);
    }

    public int CapacitySeconds => capacity;

    /// <summary>Records values for the second containing <paramref name="timestamp"/>. A later sample in the same second wins.</summary>
    public void Record(DateTimeOffset timestamp, IReadOnlyDictionary<string, double> values)
    {
        var second = timestamp.ToUnixTimeSeconds();
        lock (gate)
        {
            if (latestSecond != long.MinValue && second <= latestSecond - capacity)
                return;

            var slot = SlotOf(second);
            if (slotSeconds[slot] != second)
            {
                slotSeconds[slot] = second;
                foreach (var data in series.Values)
                    data[slot] = double.NaN;
            }

            foreach (var (id, value) in values)
            {
                if (!double.IsFinite(value))
                    continue;
                if (!series.TryGetValue(id, out var data))
                {
                    data = new double[capacity];
                    Array.Fill(data, double.NaN);
                    series[id] = data;
                }
                data[slot] = value;
            }

            if (second > latestSecond)
            {
                latestSecond = second;
                // Once per lap, forget sensors that have no samples left in the window.
                if (slot == 0)
                    PruneEmptySeries();
            }
        }
    }

    /// <summary>
    /// Builds the <c>history</c> message: every series starts at the oldest recorded second and ends at the newest,
    /// with <c>null</c> for seconds without a sample. Series without any sample are left out.
    /// </summary>
    /// <param name="ids">Only include these sensors, e.g. the current catalog. <c>null</c> includes all.</param>
    public HistoryMessage ToMessage(IEnumerable<string>? ids = null)
    {
        lock (gate)
        {
            if (latestSecond == long.MinValue)
                return new HistoryMessage(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, new Dictionary<string, double?[]>());

            var start = latestSecond;
            for (var second = latestSecond - capacity + 1; second <= latestSecond; second++)
            {
                if (slotSeconds[SlotOf(second)] == second)
                {
                    start = second;
                    break;
                }
            }

            var length = (int)(latestSecond - start + 1);
            var wanted = ids is null ? null : new HashSet<string>(ids, StringComparer.Ordinal);
            var result = new Dictionary<string, double?[]>(StringComparer.Ordinal);
            foreach (var (id, data) in series)
            {
                if (wanted is not null && !wanted.Contains(id))
                    continue;

                var column = new double?[length];
                var any = false;
                for (var i = 0; i < length; i++)
                {
                    var second = start + i;
                    var slot = SlotOf(second);
                    if (slotSeconds[slot] == second && !double.IsNaN(data[slot]))
                    {
                        column[i] = data[slot];
                        any = true;
                    }
                }

                if (any)
                    result[id] = column;
            }

            return new HistoryMessage(start, 1, result);
        }
    }

    private int SlotOf(long second) => (int)(((second % capacity) + capacity) % capacity);

    private void PruneEmptySeries()
    {
        var oldest = latestSecond - capacity + 1;
        List<string>? empty = null;
        foreach (var (id, data) in series)
        {
            var hasSample = false;
            for (var slot = 0; slot < capacity && !hasSample; slot++)
                hasSample = slotSeconds[slot] >= oldest && !double.IsNaN(data[slot]);
            if (!hasSample)
                (empty ??= []).Add(id);
        }

        empty?.ForEach(id => series.Remove(id));
    }
}
