using System.Runtime.InteropServices;
using CommandLine;
using Google.OrTools.LinearSolver;

namespace TagFilter
{
    internal class Options
    {
        [Option(Required = true, HelpText = "Path to input files containing tags")]
        public string Tags { get; set; } = "";

        [Option(Required = false, HelpText = "Directory to copy selected images to")]
        public string? OutDir { get; set; }

        [Option(Default = 250, HelpText = "Minimum number of occurrences in the data for a tag to be required")]
        public int Threshold { get; set; }

        [Option(Default = 100, HelpText = "Minimum number of occurrences of each required tag in the result")]
        public int Minimum { get; set; }

        [Option]
        public bool Exact { get; set; }
    }

    internal class Program
    {
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes
        );

        class FileInfo
        {
            public string FilePath;
            public List<string> Tags;
            public Variable Column;
        }

        private static readonly HashSet<string> IgnoredTags = new HashSet<string>()
        {
            "absurdres",
            "alternate breast size",
            "alternate hairstyle",
            "artist request",
            "bad tumblr id",
            "bad twitter id",
            "borrowed character",
            "character request",
            "copyright request",
            "fanbox reward",
            "has bad revision",
            "has censored revision",
            "highres",
            "korean commentary",
            "mixed-language commentary",
            "paid reward available",
            "partial commentary",
            "patreon reward",
            "pixiv commission",
            "resolution mismatch",
            "second-party source",
            "source request",
            "skeb commission",
            "symbol-only commentary",
            "third-party edit",
            "variant set",
        };

        static bool SequenceSubset(IEnumerable<int> left, IEnumerable<int> right)
        {
            IEnumerator<int> rightEnum = right.GetEnumerator();
            rightEnum.MoveNext();
            foreach (int leftElem in left)
            {
                while (rightEnum.Current < leftElem)
                    rightEnum.MoveNext();

                if (rightEnum.Current != leftElem)
                    return false;
            }

            return true;
        }

        static void Main(string[] args)
        { 
            var tagCounts = new Dictionary<string, int>();
            var tagConstraint = new Dictionary<string, Constraint>();
            var fileInfos = new List<FileInfo>();

            ParserResult<Options> parsed = Parser.Default.ParseArguments<Options>(args);
            Options options = parsed.Value ?? new Options();

            if (parsed.Errors.Any())
                return;

            int minCountToInclude = options.Threshold;
            int minExampleCount = options.Minimum;

            Console.WriteLine("Loading tag data...");
            string directoryName, searchPattern;
            if (Directory.Exists(options.Tags))
            {
                directoryName = options.Tags;
                searchPattern = "*.txt";
            }
            else
            {
                directoryName = Path.GetDirectoryName(options.Tags) ?? Directory.GetCurrentDirectory();
                searchPattern = Path.GetFileName(options.Tags);
            }

            foreach (string filePath in Directory.GetFiles(directoryName, searchPattern))
            {
                List<string> tags = File.ReadAllLines(filePath).SelectMany(line => line.Split(new[] { ',' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
                foreach (string tag in tags.Except(IgnoredTags))
                {
                    tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
                }

                fileInfos.Add(new FileInfo { FilePath = filePath, Tags = tags});
            }

            Console.WriteLine("Generating model...");

            Solver solver = Solver.CreateSolver("SCIP");
            Objective objective = solver.Objective();
            objective.SetMinimization();

            solver.EnableOutput();
            if (!options.Exact)
                solver.SetSolverSpecificParametersAsString("limits/gap=0.01");

            foreach (KeyValuePair<string, int> kvp in tagCounts.Where(kvp => kvp.Value >= minCountToInclude)
                         .OrderBy(kvp => kvp.Value))
            {
                tagConstraint[kvp.Key] = solver.MakeConstraint(minExampleCount, double.PositiveInfinity, kvp.Key);
            }

            foreach (FileInfo fileInfo in fileInfos)
            {
                var column = solver.MakeBoolVar(fileInfo.FilePath);
                fileInfo.Column = column;

                objective.SetCoefficient(column, 1);   

                foreach (string tag in fileInfo.Tags)
                {
                    if (tagConstraint.TryGetValue(tag, out Constraint row))
                        row.SetCoefficient(column, 1);
                }
            }

            Console.WriteLine("Finished generating model");

            var result = solver.Solve();
            Console.WriteLine($"Integer result: {result}");
            double integerObjective = objective.Value();
            Console.WriteLine($"Integer objective: {integerObjective}");

            string[] chosenPaths = fileInfos.Where(f => f.Column.SolutionValue() > 0.0).Select(f => f.FilePath).ToArray();

            using var file = new StreamWriter("output.txt");
            foreach (string path in chosenPaths)
                file.WriteLine(path);

            if (options.OutDir != null)
            {
                Console.WriteLine("Creating hardlinks...");
                CreateHardlinks(chosenPaths, options.Tags, options.OutDir);
            }
        }

        private static void CreateHardlinks(IEnumerable<string> chosenPaths, string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string path in chosenPaths)
            {
                foreach (string oldPath in Directory.GetFiles(Path.GetDirectoryName(path) ?? sourceDir,
                             Path.GetFileNameWithoutExtension(path) + ".*"))
                {
                    string newPath = Path.Join(destDir, Path.GetFileName(oldPath));
                    CreateHardLink(newPath, oldPath, IntPtr.Zero);
                }
            }
        }

    }
}