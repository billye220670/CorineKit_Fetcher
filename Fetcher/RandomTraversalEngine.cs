using System;
using System.Collections.Generic;
using System.IO;

namespace ImageSelector
{
    /// <summary>
    /// 全覆盖随机遍历引擎，取代原先的 Random.Next 纯随机抽选。
    ///
    /// 设计目标（针对"重复率高、角落抽不到"）：
    /// 1. 全覆盖且无重复：以"轮(epoch)"为单位发放，一轮内每张图片恰好被抽中一次，
    ///    从机制上消除纯随机带来的反复命中与长期遗漏。
    /// 2. 目录分层 + 饥饿优先轮转：每个子目录一个牌堆，每次抽选挑"本轮剩余比例最高"
    ///    （最饥饿）的目录并叠加随机抖动。图片多的大目录不会长期霸屏，深层只有几张图的
    ///    小目录也会按比例稳定出现，不会变成抽不到的角落。
    /// 3. 高跳跃洗牌：目录内部用"互质步长环形交织 + 受约束随机扰动"生成出牌顺序，
    ///    保证相邻两次抽到的文件在原始目录顺序上拉开足够距离
    ///    （形如 2→7→3→9…，而不是 1→2→4→8…）。
    /// </summary>
    internal sealed class RandomTraversalEngine
    {
        /// <summary>
        /// 单个目录的牌堆。All 是该目录下全部图片的全局索引，Pending 是本轮洗牌后的出牌顺序。
        /// </summary>
        private sealed class Deck
        {
            public int Ordinal;
            public string Directory = string.Empty;
            public readonly List<int> All = new List<int>();
            public List<int> Pending = new List<int>();
            public int Position;
            public int Remaining;
        }

        private readonly List<Deck> decks = new List<Deck>();
        private readonly Random rng = new Random();
        private int fileCount;
        private int lastServedIndex = -1;
        private int lastDeckOrdinal = -1;

        /// <summary>已经完成的轮数（从 1 开始计）。</summary>
        public int Epoch { get; private set; }

        /// <summary>本轮还剩多少张没抽到，归零时自动开启新一轮。</summary>
        public int RemainingInEpoch { get; private set; }

        /// <summary>参与遍历的目录数量。</summary>
        public int DirectoryCount => decks.Count;

        /// <summary>参与遍历的图片总数。</summary>
        public int FileCount => fileCount;

        /// <summary>
        /// 用新的文件列表重建所有牌堆。切换目录时必须调用。
        /// </summary>
        public void Rebuild(IList<string> imageFiles)
        {
            decks.Clear();
            fileCount = imageFiles?.Count ?? 0;
            lastServedIndex = -1;
            lastDeckOrdinal = -1;
            Epoch = 0;
            RemainingInEpoch = 0;
            if (imageFiles == null || fileCount == 0) return;

            var byDirectory = new Dictionary<string, Deck>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < imageFiles.Count; i++)
            {
                string dir;
                try
                {
                    dir = Path.GetDirectoryName(imageFiles[i]) ?? string.Empty;
                }
                catch (Exception)
                {
                    dir = string.Empty;
                }

                if (!byDirectory.TryGetValue(dir, out Deck? deck) || deck == null)
                {
                    deck = new Deck { Ordinal = byDirectory.Count, Directory = dir };
                    byDirectory.Add(dir, deck);
                    decks.Add(deck);
                }
                deck.All.Add(i);
            }

            StartEpoch();
        }

        /// <summary>
        /// 开启新一轮：把每个目录的牌堆重新洗牌并复位。
        /// </summary>
        private void StartEpoch()
        {
            foreach (var deck in decks)
            {
                deck.Pending = SpacedShuffle(deck.All, rng);
                deck.Position = 0;
                deck.Remaining = deck.All.Count;
            }
            RemainingInEpoch = fileCount;
            Epoch++;
        }

        /// <summary>
        /// 取出下一张图片的全局索引；文件列表为空时返回 -1。
        /// </summary>
        public int Next()
        {
            if (fileCount == 0 || decks.Count == 0) return -1;
            if (RemainingInEpoch <= 0) StartEpoch();

            Deck? deck = SelectDeck();
            if (deck == null || deck.Position >= deck.Pending.Count) return -1;

            // 跨轮衔接：新一轮的第一张尽量不要与上一轮的最后一张相同或在目录里紧挨着，
            // 否则两轮交界处会出现"…9 8…"这种断掉的跳跃感。
            if (deck.Remaining > 1 && lastServedIndex >= 0
                && Math.Abs(deck.Pending[deck.Position] - lastServedIndex) <= 1)
            {
                for (int tries = 0; tries < 6; tries++)
                {
                    int swapWith = deck.Position + 1 + rng.Next(deck.Remaining - 1);
                    if (Math.Abs(deck.Pending[swapWith] - lastServedIndex) > 1)
                    {
                        Swap(deck.Pending, deck.Position, swapWith);
                        break;
                    }
                }
            }

            int index = deck.Pending[deck.Position++];
            deck.Remaining--;
            RemainingInEpoch--;
            lastServedIndex = index;
            lastDeckOrdinal = deck.Ordinal;
            return index;
        }

        /// <summary>
        /// 选出本次要发牌的目录：饥饿度（本轮剩余比例）最高者胜，
        /// 并叠加随机抖动与"刚抽过就降权"，兼顾均匀覆盖与目录间的跳跃感。
        /// </summary>
        private Deck? SelectDeck()
        {
            Deck? best = null;
            double bestScore = double.MinValue;

            for (int i = 0; i < decks.Count; i++)
            {
                Deck deck = decks[i];
                if (deck.Remaining <= 0) continue;

                double hunger = (double)deck.Remaining / deck.All.Count;
                double score = hunger * (0.72 + 0.56 * rng.NextDouble());

                // 刚抽过的目录降权，让相邻两次尽量落在不同目录
                if (decks.Count > 1 && deck.Ordinal == lastDeckOrdinal) score *= 0.5;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = deck;
                }
            }

            return best;
        }

        /// <summary>
        /// 高跳跃洗牌。先按与 n 互质的步长做环形交织（天然把相邻文件拉开约 0.6n 的距离），
        /// 再施加大量"不破坏最小间距"的随机对换来打散规律性。
        /// 结果既保持随机，又保证连续两张在原始顺序上不会挨着。
        /// </summary>
        private static List<int> SpacedShuffle(List<int> source, Random rng)
        {
            int n = source.Count;
            var result = new List<int>(n);
            if (n == 0) return result;

            // 洗牌在"位置"层面进行，最后再映射回全局索引，
            // 这样间距衡量的是文件在目录中的真实先后关系。
            if (n <= 2)
            {
                for (int i = 0; i < n; i++) result.Add(i);
                if (n == 2 && rng.Next(2) == 0) Swap(result, 0, 1);
                return MapToValues(result, source);
            }

            int stride = PickStride(n, rng);

            // 用 long 累加，避免 n 很大时 k * stride 溢出 int
            long cursor = rng.Next(n);
            for (int k = 0; k < n; k++)
            {
                result.Add((int)(cursor % n));
                cursor += stride;
            }

            // 以基础排列已经保证的间距为上限，取 n/3 作为期望下限，两者取小作为扰动红线
            int achievable = MinAdjacentGap(result);
            int desired = (int)Math.Ceiling(n / 3.0);
            int minGap = Math.Max(1, Math.Min(achievable, desired));

            int iterations = Math.Min(n * 4, 400000);
            for (int t = 0; t < iterations; t++)
            {
                int i = rng.Next(n);
                int j = rng.Next(n);
                if (i == j) continue;

                Swap(result, i, j);
                // 交换只影响 i、j 两处邻接关系，局部校验即可；不合格就回滚
                if (!GapOk(result, i, minGap) || !GapOk(result, j, minGap)) Swap(result, i, j);
            }

            return MapToValues(result, source);
        }

        private static List<int> MapToValues(List<int> positions, List<int> source)
        {
            var values = new List<int>(positions.Count);
            for (int i = 0; i < positions.Count; i++) values.Add(source[positions[i]]);
            return values;
        }

        /// <summary>
        /// 挑选一个与 n 互质、且落在 [0.3n, 0.75n] 区间的步长。
        /// 互质保证环形交织能遍历全部元素；区间限制避开 1 和 n-1 这类"退化成顺序/倒序"的步长。
        /// </summary>
        private static int PickStride(int n, Random rng)
        {
            if (n <= 2) return 1;

            int ideal = Math.Max(2, (int)Math.Round(n * 0.6180339887));
            int lo = Math.Max(2, (int)(n * 0.30));
            int hi = Math.Min(n - 1, (int)(n * 0.75));
            if (hi < lo)
            {
                lo = 2;
                hi = n - 1;
            }

            var candidates = new List<int>();
            for (int s = lo; s <= hi; s++)
            {
                if (Gcd(s, n) == 1) candidates.Add(s);
            }

            // 首选区间内找不到互质步长时放宽到全部可行步长
            if (candidates.Count == 0)
            {
                for (int s = 2; s < n; s++)
                {
                    if (Gcd(s, n) == 1) candidates.Add(s);
                }
            }
            if (candidates.Count == 0) return 1;

            candidates.Sort((a, b) => Math.Abs(a - ideal).CompareTo(Math.Abs(b - ideal)));
            return candidates[rng.Next(Math.Min(candidates.Count, 3))];
        }

        private static int MinAdjacentGap(List<int> order)
        {
            int n = order.Count;
            if (n < 2) return int.MaxValue;

            int min = int.MaxValue;
            for (int i = 0; i < n - 1; i++)
            {
                int gap = Math.Abs(order[i] - order[i + 1]);
                if (gap < min) min = gap;
            }
            return min;
        }

        private static bool GapOk(List<int> order, int index, int minGap)
        {
            int n = order.Count;
            if (n < 3 || minGap <= 1) return true;

            if (index > 0 && Math.Abs(order[index] - order[index - 1]) < minGap) return false;
            if (index < n - 1 && Math.Abs(order[index] - order[index + 1]) < minGap) return false;
            return true;
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a;
        }

        private static void Swap(List<int> list, int i, int j)
        {
            int tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }
}
