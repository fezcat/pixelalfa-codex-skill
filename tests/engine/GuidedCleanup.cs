namespace PixelAlfa;

enum CleanupMark { Remove, Protect }

static class GuidedCleanup
{
    public static CleanupResult Find(int[] pixels, int width, IReadOnlyDictionary<int, CleanupMark> marks,
        int tolerance, CancellationToken token, bool followEdges = true, bool refineInterior = true)
    {
        if (!marks.Values.Contains(CleanupMark.Remove))
            return new([], "Öneri için temizlenecek alana bir işaret koy.");

        int threshold = tolerance * tolerance;
        int[] remove = Distances(CleanupMark.Remove);
        int[] protect = Distances(CleanupMark.Protect);
        var indices = new List<int>();
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (remove[i] != 0 && (protect[i] == 0 || remove[i] < protect[i])) indices.Add(i);
        }
        if (indices.Count == 0) return new([], "İşaretlerden temizlenecek görünür alan bulunamadı.");

        int initialCount = indices.Count;
        if (followEdges && refineInterior && tolerance > 0 && marks.Values.Contains(CleanupMark.Protect))
            RecoverInterior(pixels, width, marks, indices, threshold, token);

        int extra = indices.Count - initialCount;
        return new([new(1, indices.ToArray(), Rectangle.Empty, RegionKind.Guided)], extra == 0
            ? "İşaretlerden öneri hazır. Farkları kontrol et ve seçimi uygula."
            : $"Kapalı iç boşlukta {extra:N0} ek fon pikseli önerildi. Farkları kontrol et ve seçimi uygula.");

        int[] Distances(CleanupMark type)
        {
            var result = new int[pixels.Length];
            var queue = new PriorityQueue<(int Index, int Color), (int Cost, int Order)>();
            int order = 0;
            foreach (var (index, mark) in marks)
            {
                if (mark != type || (uint)pixels[index] >> 24 == 0) continue;
                result[index] = 1;
                queue.Enqueue((index, pixels[index]), (1, order++));
            }

            int steps = 0;
            while (queue.TryDequeue(out var item, out var priority))
            {
                if ((steps++ & 4095) == 0) token.ThrowIfCancellationRequested();
                int i = item.Index, x = i % width;
                if (priority.Cost != result[i]) continue;
                if (x > 0) Visit(i, i - 1, item.Color);
                if (x + 1 < width) Visit(i, i + 1, item.Color);
                if (i >= width) Visit(i, i - width, item.Color);
                if (i + width < pixels.Length) Visit(i, i + width, item.Color);
            }
            return result;

            void Visit(int from, int i, int color)
            {
                if ((uint)pixels[i] >> 24 == 0) return;
                int seedDifference = LocalCleanup.RgbDistanceSquared(pixels[i], color);
                if (seedDifference > threshold) return;
                if (marks.TryGetValue(i, out CleanupMark mark) && mark != type) return;
                // A minimum-cost path stays near the sampled color and pays
                // extra for crossing a strong edge between adjacent pixels.
                int step = followEdges
                    ? 16 + LocalCleanup.RgbDistanceSquared(pixels[i], pixels[from]) / 32 + seedDifference / 32
                    : 1;
                if (result[from] > int.MaxValue - step) return;
                int distance = result[from] + step;
                if (result[i] != 0 && result[i] <= distance) return;
                result[i] = distance;
                queue.Enqueue((i, color), (distance, order++));
            }
        }
    }

    private static void RecoverInterior(int[] pixels, int width, IReadOnlyDictionary<int, CleanupMark> marks,
        List<int> indices, int threshold, CancellationToken token)
    {
        int height = pixels.Length / width;
        if (width < 3 || height < 3) return;

        int[] removeColors = Colors(CleanupMark.Remove), protectColors = Colors(CleanupMark.Protect);
        if (removeColors.Length == 0 || protectColors.Length == 0) return;

        // Freeze the first pass. Added candidates cannot expose more pixels or train a new color.
        var selected = new bool[pixels.Length];
        var open = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            open[i] = (uint)pixels[i] >> 24 == 0;
        }
        foreach (int i in indices) selected[i] = open[i] = true;

        var interior = new bool[pixels.Length];
        var component = new List<int>();
        bool foundInterior = false;
        foreach (var (seed, mark) in marks)
        {
            if (mark != CleanupMark.Remove || !selected[seed] || !open[seed]) continue;
            component.Clear();
            LocalCleanup.Seed(open, component, seed);
            Rectangle bounds = LocalCleanup.Flood(open, component, width, height, token);
            if (bounds.Left == 0 || bounds.Top == 0 || bounds.Right == width || bounds.Bottom == height) continue;
            foreach (int i in component) interior[i] = true;
            foundInterior = true;
        }
        if (!foundInterior) return;

        for (int y = 1; y < height - 1; y++)
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                if ((i & 4095) == 0 || x == 1) token.ThrowIfCancellationRequested();
                if (selected[i] || !Opaque(i) || marks.ContainsKey(i) ||
                    !(interior[i - 1] || interior[i + 1] || interior[i - width] || interior[i + width]) ||
                    OutsideOpening(i - 1) || OutsideOpening(i + 1) || OutsideOpening(i - width) || OutsideOpening(i + width))
                    continue;

                int sampleDistance = StrongSample(i);
                int nearestRemoved = int.MaxValue, nearestKept = int.MaxValue, support = 0;
                bool touchesRemoval = false, hasNearbyOtherTone = false;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int neighbor = i + dy * width + dx;
                        if ((uint)pixels[neighbor] >> 24 == 0) continue;
                        int distance = LocalCleanup.RgbDistanceSquared(pixels[i], pixels[neighbor]);
                        if (selected[neighbor] && Opaque(neighbor) && interior[neighbor])
                        {
                            if (distance > threshold) continue;
                            support++;
                            touchesRemoval |= dx == 0 || dy == 0;
                            nearestRemoved = Math.Min(nearestRemoved, distance);
                        }
                        else if (!selected[neighbor])
                        {
                            nearestKept = Math.Min(nearestKept, distance);
                            if (sampleDistance >= 0 && distance <= 2 * sampleDistance && StrongSample(neighbor) < 0)
                                hasNearbyOtherTone = true;
                        }
                    }

                // Detached patches need a close marked color. A connected shade can instead
                // use two nearby removals, but must be closer to them than to any kept tone.
                if ((sampleDistance >= 0 && !hasNearbyOtherTone) ||
                    (touchesRemoval && support >= 2 && 2 * nearestRemoved < nearestKept &&
                        !NearProtected(i, 2 * nearestRemoved)))
                    indices.Add(i);
            }

        bool Opaque(int index) => (uint)pixels[index] >> 24 == 255;

        bool OutsideOpening(int index) => !interior[index] && (selected[index] || (uint)pixels[index] >> 24 == 0);

        int[] Colors(CleanupMark mark) => marks.Where(entry => entry.Value == mark && Opaque(entry.Key))
            .Select(entry => pixels[entry.Key]).Distinct().ToArray();

        bool NearProtected(int index, int distance)
        {
            foreach (int color in protectColors)
                if (LocalCleanup.CloseRgb(pixels[index], color, distance)) return true;
            return false;
        }

        int StrongSample(int index)
        {
            if (!Opaque(index) || marks.ContainsKey(index)) return -1;
            int closest = threshold / 9; // Detached matches use one third of the RGB tolerance.
            bool found = false;
            foreach (int color in removeColors)
            {
                int distance = LocalCleanup.RgbDistanceSquared(pixels[index], color);
                if (distance > closest) continue;
                closest = distance;
                found = true;
            }
            return found && !NearProtected(index, 2 * closest) ? closest : -1;
        }
    }
}
