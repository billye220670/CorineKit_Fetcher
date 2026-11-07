using System;
using System.Collections.Generic;
using System.IO;

namespace ImageSelector
{
    /// <summary>
    /// 目录优先的随机遍历引擎。
    ///
    /// 调度单位是叶子目录，而不是单张图片：
    /// 1. 每次抽选先按权重选一个目录：权重 = √图片数 × 最近访问衰减。
    ///    图片多的目录仍然曝光更多，但不会长期霸屏；深层只有一两张图的小目录也能稳定出现。
    /// 2. 目录软冷却：刚出过图的目录权重压到下限，随经过的抽选次数线性恢复。
    ///    目录不会被永久标记为"已看过"，始终保留再次被抽中的概率。
    /// 3. 目录内部按洗牌顺序轮换图片，一圈走完再重新洗牌，同一目录的图片不会连续重复。
    /// 4. 相邻两次抽选不落在同一个目录（只要还有别的目录可选）。
    /// </summary>
    internal sealed class RandomTraversalEngine
    {
        /// <summary>
        /// 单个叶子目录的牌堆。All 是该目录下全部图片的全局索引，Order 是本圈洗牌后的出牌顺序。
        /// </summary>
        private sealed class LeafDeck
        {
            public string Directory = string.Empty;
            public readonly List<int> All = new List<int>();
            public List<int> Order = new List<int>();
            public int Position;
            public long LastServedTick = -1;
            public double BaseWeight;
        }

        /// <summary>目录基础权重 = 图片数 ^ 该指数。0.5 即按图片数开方，兼顾大目录曝光与路径多样性。</summary>
        private const double WeightExponent = 0.5;

        /// <summary>软冷却长度：目录出牌后，权重在这么多次抽选内线性恢复到 1。</summary>
        private const double CooldownDraws = 24.0;

        /// <summary>软冷却下限：刚出过牌的目录权重最多压到这个比例，保证仍有几率回来。</summary>
        private const double MinCooldownWeight = 0.05;

        private readonly List<LeafDeck> decks = new List<LeafDeck>();
        private readonly Random rng = new Random();
        private double[] weights = Array.Empty<double>();
        private int fileCount;
        private LeafDeck? lastDeck;
        private long tick;

        /// <summary>参与遍历的目录数量。</summary>
        public int DirectoryCount => decks.Count;

        /// <summary>参与遍历的图片总数。</summary>
        public int FileCount => fileCount;

        /// <summary>
        /// 用新的文件列表重建所有目录牌堆。切换目录时必须调用。
        /// </summary>
        public void Rebuild(IList<string> imageFiles)
        {
            decks.Clear();
            weights = Array.Empty<double>();
            lastDeck = null;
            tick = 0;
            fileCount = imageFiles?.Count ?? 0;
            if (imageFiles == null || fileCount == 0) return;

            var byDirectory = new Dictionary<string, LeafDeck>(StringComparer.OrdinalIgnoreCase);
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

                if (!byDirectory.TryGetValue(dir, out LeafDeck? deck) || deck == null)
                {
                    deck = new LeafDeck { Directory = dir };
                    byDirectory.Add(dir, deck);
                    decks.Add(deck);
                }
                deck.All.Add(i);
            }

            foreach (var deck in decks)
            {
                deck.BaseWeight = Math.Pow(deck.All.Count, WeightExponent);
                deck.Order = Shuffle(deck.All);
                deck.Position = 0;
            }
            weights = new double[decks.Count];
        }

        /// <summary>
        /// 取出下一张图片的全局索引；文件列表为空时返回 -1。
        /// </summary>
        public int Next()
        {
            if (fileCount == 0 || decks.Count == 0) return -1;

            LeafDeck deck = SelectDeck();
            int index = deck.Order[deck.Position++];

            if (deck.Position >= deck.Order.Count)
            {
                deck.Order = Shuffle(deck.All);
                deck.Position = 0;
                // 新一圈的第一张不要和刚出过的图撞上
                if (deck.Order.Count > 1 && deck.Order[0] == index)
                {
                    Swap(deck.Order, 0, rng.Next(1, deck.Order.Count));
                }
            }

            deck.LastServedTick = tick++;
            lastDeck = deck;
            return index;
        }

        /// <summary>
        /// 按"基础权重 × 软冷却"做加权随机选目录。
        /// 加权随机而不是取最高分，避免确定性取最大值把冷落目录的回归挤成阵发。
        /// </summary>
        private LeafDeck SelectDeck()
        {
            double total = 0;
            for (int i = 0; i < decks.Count; i++)
            {
                LeafDeck deck = decks[i];
                // 相邻两次不落在同一目录，除非没有别的目录可选
                double weight = deck == lastDeck && decks.Count > 1 ? 0.0 : Weight(deck);
                weights[i] = weight;
                total += weight;
            }

            if (total <= 0) return decks[rng.Next(decks.Count)];

            double pick = rng.NextDouble() * total;
            for (int i = 0; i < decks.Count; i++)
            {
                pick -= weights[i];
                if (pick <= 0) return decks[i];
            }
            return decks[decks.Count - 1];
        }

        /// <summary>
        /// 距离上次出牌越近权重越低，冷却期内线性恢复到 1。
        /// 下限保证刚出过牌的目录仍有被再次抽中的几率，而不是被标记为"已看过"。
        /// </summary>
        private double Weight(LeafDeck deck)
        {
            if (deck.LastServedTick < 0) return deck.BaseWeight;

            double age = tick - deck.LastServedTick;
            if (age >= CooldownDraws) return deck.BaseWeight;

            return deck.BaseWeight * (MinCooldownWeight + (1.0 - MinCooldownWeight) * (age / CooldownDraws));
        }

        private List<int> Shuffle(List<int> source)
        {
            var result = new List<int>(source);
            for (int i = result.Count - 1; i > 0; i--)
            {
                Swap(result, i, rng.Next(i + 1));
            }
            return result;
        }

        private static void Swap(List<int> list, int i, int j)
        {
            int tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }
}
