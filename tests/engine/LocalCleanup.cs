namespace PixelAlfa;

enum RegionKind { Exterior, ColorMatch, Click, Reference, SampledColor, InteriorResidue, FilledInterior, Guided, Contour, Outline, InteriorEdgeReview }

sealed record Region(int Id, int[] Pixels, Rectangle Bounds, RegionKind Kind, bool OnBorder = false, int[]? Colors = null)
{
    private const int AlphaMask = unchecked((int)0xff000000);

    public bool Suggested => Kind is not (RegionKind.ColorMatch or RegionKind.FilledInterior or RegionKind.InteriorEdgeReview);

    public string Evidence => Kind switch
    {
        RegionKind.FilledInterior => "İncele · Saydamlığa değmeyen tek renkli alan. Çizim detayı da olabilir.",
        RegionKind.ColorMatch => "İncele · Fonla benzer renk. Bölgenin içeriğini kontrol et.",
        RegionKind.InteriorResidue => "Güçlü işaret · İç boşlukta tekrar eden renk ve bağlı yakın tonlar.",
        RegionKind.InteriorEdgeReview => "İncele · Komşu çizim tonuyla rengi düzeltir ve opaklığı korur.",
        RegionKind.Exterior => "Güçlü işaret · Kenar ve köşelerden desteklenen ortak fon rengi.",
        RegionKind.Guided => "İşaretlerin · Benzer renkleri ve kapalı iç boşluktaki kalıntıları arar.",
        RegionKind.Reference => "Referans · Hizalı PNG’deki alfa daha düşük.",
        RegionKind.Contour or RegionKind.Outline => "Kontur · Seçilen sınırda ekleme veya renk değişimi.",
        _ => "Renk seçimin · Benzer pikseller. Ortak çizim renklerini kontrol et."
    };

    public int Output(int offset, int current, int reference)
    {
        if (Kind == RegionKind.InteriorEdgeReview)
            return Colors is { } tones ? (current & AlphaMask) | (tones[offset] & 0x00ffffff) : current;
        if (Colors is not null) return Colors[offset];
        if (Kind != RegionKind.Reference) return 0;
        uint alpha = Math.Min((uint)current >> 24, (uint)reference >> 24);
        return alpha == 0 ? 0 : (current & 0x00ffffff) | (int)(alpha << 24);
    }

    public string Label => Kind switch
    {
            RegionKind.Exterior => "Dış fon",
            RegionKind.Reference => "Referans alfası",
            RegionKind.Click => "Tıklanan alan",
            RegionKind.SampledColor => "Örneklenen renk",
            RegionKind.InteriorResidue => "İç fon kalıntıları",
            RegionKind.InteriorEdgeReview => "Kenar adayı",
            RegionKind.FilledInterior => "Dolu iç alan",
            RegionKind.Guided => "İşaretlerden temizleme",
            RegionKind.Contour => "Kontur onarımı",
            RegionKind.Outline => "Parça konturu",
            _ => OnBorder ? "Kenarda" : "İç / ayrık"
    };

    public override string ToString()
    {
        string location = Kind is RegionKind.Click or RegionKind.ColorMatch or RegionKind.InteriorEdgeReview ? $" · {Bounds.X},{Bounds.Y}" : "";
        return $"{Label} · {Pixels.Length:N0} px{location}";
    }
}

sealed record CleanupResult(List<Region> Regions, string Message, Color? Matte = null);

static class LocalCleanup
{
    private const int CandidateLimit = 48;
    private const int InteriorTolerance = 20;
    public const int DefaultTolerance = 44;

    private struct InteriorColor
    {
        public int Total, Inner, Outer;
        public long Red, Green, Blue;
    }

    public static CleanupResult Automatic(int[] pixels, int width, int height, CancellationToken token = default, int tolerance = DefaultTolerance)
    {
        token.ThrowIfCancellationRequested();
        int? matte = BorderMatte(pixels, width, height);
        if (matte is null)
            return TransparentResidue(pixels, width, height, tolerance, token)
                ?? new([], "Kenarlar tek bir fon rengi göstermiyor. Bir alanı tıkla veya kalıntıdan renk örnekle.");

        var matching = Match(pixels, matte.Value, 0, token, out int visible);
        var exterior = new List<int>();
        foreach (int index in Perimeter(width, height))
            Seed(matching, exterior, index);
        Rectangle bounds = Flood(matching, exterior, width, height, token);
        Color color = Color.FromArgb(matte.Value);
        if (exterior.Count == visible)
            return new([], "Fon ile çizim ayırt edilemedi. Görüntünün tamamı otomatik seçilmedi.", color);

        var outside = new Region(0, exterior.ToArray(), bounds, RegionKind.Exterior, true);
        if (tolerance > 0)
        {
            // Keep the automatic exterior edit exact. The wider mask finds
            // optional inside candidates without including the outside twice.
            matching = Match(pixels, matte.Value, tolerance, token, out _);
            exterior.Clear();
            foreach (int index in Perimeter(width, height))
                Seed(matching, exterior, index);
            Flood(matching, exterior, width, height, token);
        }
        var regions = Components(matching, width, height, token, CandidateLimit - 1, out int remaining);
        regions.Insert(0, outside);
        string detail = remaining == 0 ? "" : $" {remaining:N0} iç veya ayrık alan kontrol için bulundu.";
        if (remaining > CandidateLimit - 1) detail += " Diğer alanları tıklayarak seçebilirsin.";
        return new(Number(regions), "Dış fon önizlemede seçili. Uygulamadan önce çizimin kenarlarını kontrol et." + detail, color);
    }

    public static CleanupResult ByColor(int[] pixels, Color matte, int tolerance, CancellationToken token, bool varyingAlpha = false)
    {
        var matching = Match(pixels, Color.FromArgb(255, matte).ToArgb(), tolerance, token, out _, varyingAlpha);
        var indices = new List<int>();
        for (int i = 0; i < matching.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (matching[i]) indices.Add(i);
        }
        return indices.Count == 0 ? new([], "Bu renge yakın görünür piksel bulunamadı.")
            : new([new(1, indices.ToArray(), Rectangle.Empty, RegionKind.SampledColor)],
                "Örneklenen renge uyan tüm pikseller önizlemede. Doğruysa Seçimi uygula’ya bas.");
    }

    public static CleanupResult FromClick(int[] pixels, int width, int height, int index, int tolerance,
        CancellationToken token, bool varyingAlpha = false)
    {
        int value = pixels[index];
        if (Alpha(value) == 0) return new([], "Bu piksel zaten şeffaf.");
        var matching = Match(pixels, value, tolerance, token, out _, varyingAlpha);
        var area = new List<int>();
        Seed(matching, area, index);
        Rectangle bounds = Flood(matching, area, width, height, token);
        return new([new(1, area.ToArray(), bounds, RegionKind.Click)],
            "Tıklanan bağlı alan önizlemede. Doğruysa seçimi uygula.", Color.FromArgb(value | unchecked((int)0xff000000)));
    }

    public static CleanupResult FromReference(int[] pixels, int[] reference, int width, int height, CancellationToken token)
    {
        var indices = new List<int>();
        int minX = width, minY = height, maxX = 0, maxY = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (Alpha(reference[i]) >= Alpha(pixels[i])) continue;
            indices.Add(i);
            int x = i % width, y = i / width;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return indices.Count == 0 ? new([], "Referans daha saydam bir piksel göstermiyor.")
            : new([new(1, indices.ToArray(), Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1), RegionKind.Reference)],
                "Referansta daha saydam olan pikseller önizlemede. Görsellerin hizasını kontrol et.");
    }

    internal static bool[] InteriorTransparency(int[] pixels, int width, int height, CancellationToken token)
    {
        var transparent = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            transparent[i] = Alpha(pixels[i]) == 0;
        }
        KeepInterior(transparent, width, height, token);
        return transparent;
    }

    internal static void KeepInterior(bool[] open, int width, int height, CancellationToken token)
    {
        var outside = new List<int>();
        foreach (int index in Perimeter(width, height))
            Seed(open, outside, index);
        Flood(open, outside, width, height, token);
    }

    private static CleanupResult? TransparentResidue(int[] pixels, int width, int height, int tolerance, CancellationToken token)
    {
        int clear = 0, total = 0;
        foreach (int index in Perimeter(width, height))
        {
            total++;
            if (Alpha(pixels[index]) == 0) clear++;
        }
        if (clear * 2 < total) return null;
        bool[] holes = InteriorTransparency(pixels, width, height, token);
        CleanupResult? interior = Array.IndexOf(holes, true) >= 0
            ? FindInteriorResidue(pixels, width, height, tolerance, holes, token)
            : null;
        interior ??= FindFilledInterior(pixels, width, height, token);
        if (interior is not null) return interior;

        // A dot surrounded by transparency can also be artwork. Use its color only
        // to surface unchecked candidates, and only when there is other visible art.
        var dots = new Dictionary<int, int>();
        int visible = 0, isolated = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            int value = pixels[i];
            uint alpha = Alpha(value);
            if (alpha == 0) continue;
            visible++;
            if (alpha != 255) continue;
            int x = i % width, y = i / width;
            bool alone = true;
            for (int ny = Math.Max(0, y - 1); alone && ny <= Math.Min(height - 1, y + 1); ny++)
                for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                    if ((nx != x || ny != y) && Alpha(pixels[ny * width + nx]) != 0) { alone = false; break; }
            if (!alone) continue;
            dots[value] = dots.GetValueOrDefault(value) + 1;
            isolated++;
            if (dots.Count > 4096) return null;
        }
        if (isolated == 0 || visible - isolated < 8) return null;
        var best = dots.MaxBy(pair => pair.Value);
        if ((long)best.Value * 100 < (long)isolated * 65) return null;
        Color matte = Color.FromArgb(best.Key);
        var matching = Match(pixels, best.Key, tolerance, token, out _);
        var regions = Components(matching, width, height, token, CandidateLimit, out _);
        return new(Number(regions), "Ayrık pikseller bulundu. Listeden incele veya Rengi tara ile tek grup olarak önizle.", matte);
    }

    private static CleanupResult? FindFilledInterior(int[] pixels, int width, int height, CancellationToken token)
    {
        var visited = new bool[pixels.Length];
        var group = new List<int>();
        var regions = new List<Region>();
        for (int start = 0; start < pixels.Length; start++)
        {
            if ((start & 4095) == 0) token.ThrowIfCancellationRequested();
            int color = pixels[start];
            if (visited[start] || Alpha(color) != 255) continue;
            group.Clear();
            group.Add(start);
            visited[start] = true;
            bool open = false;
            for (int cursor = 0; cursor < group.Count; cursor++)
            {
                if ((cursor & 4095) == 0) token.ThrowIfCancellationRequested();
                int i = group[cursor], x = i % width;
                Visit(x > 0 ? i - 1 : -1);
                Visit(x + 1 < width ? i + 1 : -1);
                Visit(i >= width ? i - width : -1);
                Visit(i + width < pixels.Length ? i + width : -1);
            }
            if (!open && group.Count >= 16)
            {
                int smallest = regions.Count < 3 ? -1 : regions.IndexOf(regions.MinBy(region => region.Pixels.Length)!);
                if (smallest < 0) regions.Add(new(0, group.ToArray(), Rectangle.Empty, RegionKind.FilledInterior));
                else if (regions[smallest].Pixels.Length < group.Count)
                    regions[smallest] = new(0, group.ToArray(), Rectangle.Empty, RegionKind.FilledInterior);
            }

            void Visit(int i)
            {
                if (i < 0 || Alpha(pixels[i]) == 0) { open = true; return; }
                if (visited[i] || pixels[i] != color) return;
                visited[i] = true;
                group.Add(i);
            }
        }
        return regions.Count == 0 ? null : new(Number(regions.OrderByDescending(region => region.Pixels.Length).ToList()),
            "Kapalı renkli alanlar bulundu. Fon olanları fark görünümünde kontrol edip işaretle.");
    }

    private static CleanupResult? FindInteriorResidue(int[] pixels, int width, int height, int tolerance,
        bool[] holes, CancellationToken token)
    {
        var contacts = new byte[pixels.Length];
        var palette = new InteriorColor[512];
        int innerPixels = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            int color = pixels[i];
            if (Alpha(color) != 255) continue;
            byte edges = 0;
            int x = i % width;
            if (x > 0) edges |= Contact(i - 1);
            if (x + 1 < width) edges |= Contact(i + 1);
            if (i >= width) edges |= Contact(i - width);
            if (i + width < pixels.Length) edges |= Contact(i + width);
            contacts[i] = edges;

            int red = (color >> 16) & 255, green = (color >> 8) & 255, blue = color & 255;
            ref InteriorColor bin = ref palette[((red >> 5) << 6) | ((green >> 5) << 3) | (blue >> 5)];
            bin.Total++;
            if ((edges & 2) != 0) bin.Outer++;
            if ((edges & 1) == 0) continue;
            innerPixels++;
            bin.Inner++;
            bin.Red += red;
            bin.Green += green;
            bin.Blue += blue;
        }

        // A background remnant should cover more than an outline and repeat along
        // an inner opening. A weak or shared palette is left for the eyedropper.
        InteriorColor[] top = palette.Where(bin => bin.Outer == 0 && bin.Inner * 4 <= bin.Total * 3)
            .OrderByDescending(bin => bin.Inner).Take(2).ToArray();
        if (top.Length == 0) return null;
        InteriorColor best = top[0];
        if (best.Inner < 8 || best.Inner * 5 < innerPixels || (top.Length == 2 && best.Inner < 2 * top[1].Inner))
            return null;

        Color matte = Color.FromArgb(Mean(best.Red), Mean(best.Green), Mean(best.Blue));
        var matching = Match(pixels, matte.ToArgb(), InteriorTolerance, token, out _);
        var group = new List<int>();
        var indices = new List<int>();
        int patches = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!matching[i]) continue;
            group.Clear();
            Seed(matching, group, i);
            Flood(matching, group, width, height, token);
            byte edges = 0;
            foreach (int pixel in group) edges |= contacts[pixel];
            if (edges != 1) continue;
            if (group.Count >= 3) patches++;
            indices.AddRange(group);
        }
        if (patches < 2) return null;
        int colorValue = matte.ToArgb();
        if (tolerance < InteriorTolerance)
            indices.RemoveAll(index => !CloseRgb(pixels[index], colorValue, tolerance * tolerance));
        else if (tolerance > InteriorTolerance)
            ExpandInterior(indices, pixels, contacts, width, height, colorValue, tolerance, token);
        if (indices.Count == 0)
            return new([], "Fon rengi bulundu. Eşleşme görmek için toleransı yükselt.", matte);
        var regions = new List<Region> { new(0, indices.ToArray(), Rectangle.Empty, RegionKind.InteriorResidue) };
        FindMixedEdges(pixels, width, height, indices, colorValue, tolerance, regions, token);
        return new(Number(regions), regions.Count == 1
            ? "İç fon kalıntıları önizlemede. Toleransı ayarla veya seçimi uygula."
            : "İç fon seçildi. Kenar adaylarını Farkları göster ile inceleyebilirsin.", matte);

        byte Contact(int index) => holes[index] ? (byte)1 : Alpha(pixels[index]) == 0 ? (byte)2 : (byte)0;
        int Mean(long sum) => (int)((sum + best.Inner / 2) / best.Inner);
    }

    private static void FindMixedEdges(int[] pixels, int width, int height, List<int> removals, int matte,
        int tolerance, List<Region> regions, CancellationToken token)
    {
        if (tolerance == 0) return;
        var open = new bool[pixels.Length];
        var removed = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            open[i] = Alpha(pixels[i]) == 0;
        }
        foreach (int i in removals) removed[i] = open[i] = true;
        KeepInterior(open, width, height, token);

        var candidates = new List<(int Index, double Error, int Color)>();
        int[] neighborOffsets = [-1, 1, -width, width];
        int maxDistance = 4 * tolerance * tolerance;
        for (int y = 1; y < height - 1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                if (removed[i] || Alpha(pixels[i]) != 255) continue;
                int distance = RgbDistanceSquared(pixels[i], matte);
                if (distance > maxDistance || !(open[i - 1] || open[i + 1] || open[i - width] || open[i + width]) ||
                    Exterior(i - 1) || Exterior(i + 1) || Exterior(i - width) || Exterior(i + width) || !NearRemoval(x, y)) continue;

                double bestError = double.PositiveInfinity;
                int bestTone = pixels[i];
                foreach (int offset in neighborOffsets)
                {
                    int neighbor = i + offset;
                    double error = Fit(i, neighbor, distance);
                    if (error >= bestError) continue;
                    bestError = error;
                    bestTone = pixels[neighbor];
                }
                if (bestError > InteriorTolerance * InteriorTolerance) continue;
                int color = MatchBrightness(pixels[i], bestTone);
                if (color != (pixels[i] & 0x00ffffff)) candidates.Add((i, bestError, color));
            }
        }
        foreach (var (index, _, color) in candidates.OrderBy(candidate => candidate.Error).Take(CandidateLimit - 1))
            regions.Add(new(0, [index], new(index % width, index / width, 1, 1), RegionKind.InteriorEdgeReview, Colors: [color]));

        bool Exterior(int i) => !open[i] && (removed[i] || Alpha(pixels[i]) == 0);

        bool NearRemoval(int x, int y)
        {
            for (int row = Math.Max(0, y - 2); row <= Math.Min(height - 1, y + 2); row++)
                for (int column = Math.Max(0, x - 2); column <= Math.Min(width - 1, x + 2); column++)
                    if (removed[row * width + column]) return true;
            return false;
        }

        double Fit(int index, int neighbor, int distance)
        {
            if (removed[neighbor] || Alpha(pixels[neighbor]) != 255) return double.PositiveInfinity;
            int edgeDistance = RgbDistanceSquared(pixels[neighbor], matte);
            // Project the pixel onto the segment from matte to artwork. Only the matte half is relevant.
            int twiceDot = distance + edgeDistance - RgbDistanceSquared(pixels[index], pixels[neighbor]);
            if (twiceDot <= 0 || twiceDot > edgeDistance) return double.PositiveInfinity;
            return distance - (double)twiceDot * twiceDot / (4 * edgeDistance);
        }
    }

    private static int MatchBrightness(int original, int tone)
    {
        // Mix the neighbor's hue with black or white so the edge keeps its contrast.
        const int white = 255 * 10_000;
        int originalBrightness = Brightness(original), toneBrightness = Brightness(tone);
        if (originalBrightness == toneBrightness) return tone & 0x00ffffff;

        bool darken = originalBrightness < toneBrightness;
        int neutral = darken ? 0 : 255;
        double strength = darken ? originalBrightness / (double)toneBrightness
            : (white - originalBrightness) / (double)(white - toneBrightness);
        int rgb = 0;
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int channel = (tone >> shift) & 255;
            rgb |= (int)Math.Round(neutral + (channel - neutral) * strength) << shift;
        }
        return rgb;

        static int Brightness(int color) => ((color >> 16) & 255) * 2126 + ((color >> 8) & 255) * 7152 + (color & 255) * 722;
    }

    private static void ExpandInterior(List<int> indices, int[] pixels, byte[] contacts, int width, int height,
        int color, int tolerance, CancellationToken token)
    {
        var selected = new bool[pixels.Length];
        foreach (int index in indices) selected[index] = true;
        int maxDistance = tolerance * tolerance;
        // Extend only from the confidently matched patches. Two nearby matches,
        // including one shared side, keep distant details out of the preview.
        for (int pass = 0; pass < 2; pass++)
        {
            int start = indices.Count;
            for (int i = 0; i < pixels.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                if (selected[i] || (contacts[i] & 2) != 0 || Alpha(pixels[i]) != 255 ||
                    !CloseRgb(pixels[i], color, maxDistance))
                    continue;
                int x = i % width, y = i / width;
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1) continue;
                int sides = (selected[i - 1] ? 1 : 0) + (selected[i + 1] ? 1 : 0) +
                    (selected[i - width] ? 1 : 0) + (selected[i + width] ? 1 : 0);
                if (sides == 0) continue;
                if (sides >= 2 || selected[i - width - 1] || selected[i - width + 1] ||
                    selected[i + width - 1] || selected[i + width + 1])
                    indices.Add(i);
            }
            if (indices.Count == start) break;
            for (int i = start; i < indices.Count; i++) selected[indices[i]] = true;
        }
    }

    internal static bool CloseRgb(int value, int color, int maxSquaredDistance) =>
        RgbDistanceSquared(value, color) <= maxSquaredDistance;

    internal static int RgbDistanceSquared(int value, int color)
    {
        int red = ((value >> 16) & 255) - ((color >> 16) & 255);
        int green = ((value >> 8) & 255) - ((color >> 8) & 255);
        int blue = (value & 255) - (color & 255);
        return red * red + green * green + blue * blue;
    }

    private static int? BorderMatte(int[] pixels, int width, int height)
    {
        if (width < 2 || height < 2) return null;
        var counts = new Dictionary<int, int>();
        int total = 0;
        foreach (int index in Perimeter(width, height))
        {
            total++;
            int value = pixels[index];
            if (Alpha(value) == 255) counts[value] = counts.GetValueOrDefault(value) + 1;
        }
        int[] corners = [pixels[0], pixels[width - 1], pixels[^width], pixels[^1]];
        foreach (int value in corners)
            if (Alpha(value) == 255 && counts[value] * 100 >= total * 65 &&
                corners.Count(corner => corner == value) >= 3)
                return value;
        return null;
    }

    private static IEnumerable<int> Perimeter(int width, int height)
    {
        for (int x = 0; x < width; x++) yield return x;
        for (int y = 1; y < height - 1; y++)
        {
            yield return y * width;
            if (width > 1) yield return y * width + width - 1;
        }
        if (height > 1)
            for (int x = (height - 1) * width; x < height * width; x++) yield return x;
    }

    private static uint Alpha(int value) => (uint)value >> 24;

    private static bool[] Match(int[] pixels, int color, int tolerance, CancellationToken token, out int visible,
        bool varyingAlpha = false)
    {
        var matching = new bool[pixels.Length];
        int max = tolerance * tolerance;
        int red = (color >> 16) & 255, green = (color >> 8) & 255, blue = color & 255;
        uint alpha = Alpha(color);
        visible = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            int value = pixels[i];
            if (Alpha(value) != 0) visible++;
            if (varyingAlpha)
            {
                matching[i] = Alpha(value) != 0 && CloseRgb(value, color, max);
                continue;
            }
            if (tolerance == 0) { matching[i] = value == color; continue; }
            if (Alpha(value) != alpha) continue;
            int dr = ((value >> 16) & 255) - red, dg = ((value >> 8) & 255) - green, db = (value & 255) - blue;
            matching[i] = dr * dr + dg * dg + db * db <= max;
        }
        return matching;
    }

    private static List<Region> Components(bool[] matching, int width, int height, CancellationToken token, int limit, out int total)
    {
        var biggest = new PriorityQueue<Region, (int Size, int Order)>();
        var queue = new List<int>();
        total = 0;
        for (int start = 0; start < matching.Length; start++)
        {
            if ((start & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!matching[start]) continue;
            queue.Clear();
            Seed(matching, queue, start);
            Rectangle bounds = Flood(matching, queue, width, height, token);
            var priority = (queue.Count, -total++);
            if (biggest.Count == limit)
            {
                if (biggest.TryPeek(out _, out var smallest) && priority.CompareTo(smallest) <= 0) continue;
                biggest.Dequeue();
            }
            bool border = bounds.Left == 0 || bounds.Top == 0 || bounds.Right == width || bounds.Bottom == height;
            biggest.Enqueue(new(0, queue.ToArray(), bounds, RegionKind.ColorMatch, border), priority);
        }
        return biggest.UnorderedItems.OrderByDescending(item => item.Priority)
            .Select(item => item.Element).ToList();
    }

    private static List<Region> Number(List<Region> regions) =>
        regions.Select((region, index) => region with { Id = index + 1 }).ToList();

    internal static void Seed(bool[] matching, List<int> queue, int index)
    {
        if (!matching[index]) return;
        matching[index] = false;
        queue.Add(index);
    }

    internal static Rectangle Flood(bool[] matching, List<int> queue, int width, int height, CancellationToken token)
    {
        int minX = width, minY = height, maxX = 0, maxY = 0;
        for (int cursor = 0; cursor < queue.Count; cursor++)
        {
            if ((cursor & 4095) == 0) token.ThrowIfCancellationRequested();
            int index = queue[cursor], x = index % width, y = index / width;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            if (x > 0) Seed(matching, queue, index - 1);
            if (x + 1 < width) Seed(matching, queue, index + 1);
            if (y > 0) Seed(matching, queue, index - width);
            if (y + 1 < height) Seed(matching, queue, index + width);
        }
        return queue.Count == 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}
