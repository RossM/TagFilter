using System.Runtime.InteropServices;
using LpSolveDotNet;

namespace TagFilter
{
    internal class Program
    {
        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(
            string lpFileName,
            string lpExistingFileName,
            IntPtr lpSecurityAttributes
        );

        struct FileInfo
        {
            public int Column;
            public string FilePath;
            public List<string> Tags;
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
            "skeb commission",
            "symbol-only commentary",
            "third-party edit",
        };

        static bool SequenceSubset(IEnumerable<int> left, IEnumerable<int> right)
        {
            var rightEnum = right.GetEnumerator();
            rightEnum.MoveNext();
            foreach (var leftElem in left)
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
            var tagVarId = new Dictionary<string, int>();
            var fileInfos = new List<FileInfo>();

            const int minCountToInclude = 250;
            const int minExampleCount = 100;

            string sourceDir = args[0];
            string? destDir = args.Length >= 2 ? args[1] : null;

            Console.WriteLine("Loading tag data...");
            foreach (string filePath in Directory.GetFiles(sourceDir, "*.txt"))
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
            var rows = 1;
            foreach (KeyValuePair<string, int> kvp in tagCounts.Where(kvp => kvp.Value >= minCountToInclude)
                         .OrderBy(kvp => kvp.Value))
            {
                tagVarId[kvp.Key] = rows++;
            }

            LpSolve.Init();
            var solver = LpSolve.make_lp(rows, fileInfos.Count + 1);
            solver.set_minim();

            for (var row = 1; row < rows; row++)
            {
                solver.set_constr_type(row, lpsolve_constr_types.GE);
                solver.set_rh(row, minExampleCount);
            }

            foreach (string tag in tagCounts.Keys)
            {
                if (tagVarId.TryGetValue(tag, out int row))
                    solver.set_row_name(row, tag);
            }

            for (var i = 0; i < fileInfos.Count; i++)
            {
                FileInfo fileInfo = fileInfos[i];
                var rowEntries = new List<int> { 0 };

                foreach (string tag in fileInfo.Tags)
                {
                    if (tagVarId.TryGetValue(tag, out int row))
                        rowEntries.Add(row);
                }

                var values = new double[rowEntries.Count];
                Array.Fill(values, 1.0);
                int[] rowNo = rowEntries.ToArray();

                int col_no = i + 1;
                solver.set_columnex(col_no, rowEntries.Count, values, rowNo);
                solver.set_col_name(col_no, Path.GetFileName(fileInfo.FilePath));
                solver.set_binary(col_no, true);
            }

            // Remove dominated rows
            {
                int colCount = solver.get_Ncolumns();
                int rowCount = solver.get_Nrows();
                var ids = new int[colCount];
                var values = new double[colCount];
                var testIds = new int[colCount];
                var testValues = new double[colCount];
                int removedRows = 0;
                for (int row = rowCount - 1; row >= 2; row--)
                {
                    int rowSize = solver.get_rowex(row, values, ids);
                    for (int testRow = row - 1; testRow >= 1; testRow--)
                    {
                        int testRowSize = solver.get_rowex(testRow, testValues, testIds);
                        if (SequenceSubset(testIds.Take(testRowSize), ids.Take(rowSize)))
                        {
                            Console.WriteLine($"{solver.get_row_name(row)} ({rowSize}) is covered by {solver.get_row_name(testRow)} ({testRowSize})");
                            solver.del_constraint(row);
                            removedRows++;
                            break;
                        }
                    }
                }

                Console.WriteLine($"Eliminated {removedRows}/{rowCount} rows");
            }

            solver.write_lp("output.lp");
            solver.set_break_at_first(true);

            // Presolve. Doesn't really do much.
            //solver.set_presolve(lpsolve_presolve.PRESOLVE_ROWS | 
            //                    lpsolve_presolve.PRESOLVE_COLS | 
            //                    lpsolve_presolve.PRESOLVE_KNAPSACK |
            //                    lpsolve_presolve.PRESOLVE_ROWDOMINATE |
            //                    lpsolve_presolve.PRESOLVE_COLDOMINATE |
            //                    lpsolve_presolve.PRESOLVE_BOUNDS |
            //                    lpsolve_presolve.PRESOLVE_PROBEFIX |
            //                    lpsolve_presolve.PRESOLVE_PROBEREDUCE |
            //                    lpsolve_presolve.PRESOLVE_LINDEP, 
            //    solver.get_presolveloops());

            //solver.set_verbose(lpsolve_verbosity.DETAILED);

            DateTime nextUpdate = DateTime.Now;
            ctrlcfunc progressFunc = (_, _) =>
            {
                if (DateTime.Now >= nextUpdate)
                {
                    nextUpdate = DateTime.Now + TimeSpan.FromMilliseconds(250);
                    long total_iter = solver.get_total_iter();
                    if (total_iter > 0)
                    {
                        Console.WriteLine($"I:{total_iter} N:{solver.get_total_nodes()} T{TimeSpan.FromSeconds(Math.Floor(solver.time_elapsed()))} G:{solver.get_working_objective().ToString("g4")}");
                    }
                }

                return false;
            };
            solver.put_abortfunc(progressFunc, (IntPtr)0);

            // Get relaxed objective value
            for (var i = 1; i < solver.get_Ncolumns(); i++)
                solver.set_int(i, false);
            lpsolve_return result = solver.solve();
            Console.WriteLine($"Relaxed result: {result}");
            double relaxedGoal = solver.get_objective();
            Console.WriteLine($"Relaxed goal: {relaxedGoal}");

            solver.set_add_rowmode(true);

            // These cuts don't seem to do anything useful... too bad.
            const int maxCuts = 0;

            // Fun with cuts!
            solver.set_verbose(lpsolve_verbosity.CRITICAL);
            for (int iter = 0; iter < maxCuts; iter++)
            {
                bool added = false;
                added |= AddIntegerObjectiveCut(solver);
                if (!added)
                    break;

                result = solver.solve();
                relaxedGoal = solver.get_objective();
            }
            solver.set_verbose(lpsolve_verbosity.NORMAL);
            solver.write_lp("cuts.lp");

            // We'll take any solution within 10% of the relaxed objective
            // solver.set_break_at_value(relaxedGoal * 1.1);

            // Anti-cycling
            solver.set_pivoting(lpsolve_pivot_rule.PRICER_DANTZIG, lpsolve_pivot_modes.PRICE_RANDOMIZE);

            // Custom MIP settings to speed up solve
            solver.set_simplextype(lpsolve_simplextypes.SIMPLEX_DUAL_DUAL);
            solver.set_bb_rule(lpsolve_BBstrategies.NODE_FRACTIONSELECT | 
                               lpsolve_BBstrategies.NODE_PSEUDOCOSTMODE |
                               lpsolve_BBstrategies.NODE_DEPTHFIRSTMODE |
                               lpsolve_BBstrategies.NODE_DYNAMICMODE |
                               lpsolve_BBstrategies.NODE_RCOSTFIXING);
            solver.set_bb_floorfirst(lpsolve_branch.BRANCH_CEILING);
            // Because the objective is an integer, we can set a large MIP gap without danger of losing a good solution
            solver.set_mip_gap(true, 0.9999);
            //solver.set_print_sol(lpsolve_print_sol_option.TRUE);

            // Do branch-and-bound search
            for (var i = 1; i < solver.get_Ncolumns(); i++)
                solver.set_int(i, true);
            result = solver.solve();
            Console.WriteLine($"Integer result: {result}");
            double integerGoal = solver.get_objective();
            Console.WriteLine($"Integer goal: {integerGoal}");

            var chosenPaths = fileInfos.Where((f, i) => solver.get_var_primalresult(1 + solver.get_Nrows() + i) > 0.0).Select(f => f.FilePath).ToArray();

            using var file = new StreamWriter("output.txt");
            foreach (var path in chosenPaths)
                file.WriteLine(path);

            if (destDir != null)
            {
                Console.WriteLine("Creating hardlinks...");
                CreateHardlinks(chosenPaths, sourceDir, destDir);
            }
        }

        private static void CreateHardlinks(IEnumerable<string> chosenPaths, string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var path in chosenPaths)
            {
                foreach (var oldPath in Directory.GetFiles(Path.GetDirectoryName(path) ?? sourceDir,
                             Path.GetFileNameWithoutExtension(path) + ".*"))
                {
                    string newPath = Path.Join(destDir, Path.GetFileName(oldPath));
                    CreateHardLink(newPath, oldPath, IntPtr.Zero);
                }
            }
        }

        private static bool AddIntegerObjectiveCut(LpSolve solver)
        {
            var indexes = new List<int>();
            double total = 0;
            //double objectiveCheck = 0;
            int colCount = solver.get_Ncolumns();
            int rowCount = solver.get_Nrows();
            for (int i = 1; i < colCount; i++)
            {
                double val = solver.get_var_primalresult(rowCount + i);
                //objectiveCheck += val;
                if (val < 1.0)
                {
                    indexes.Add(i);
                    total += val;
                }
            }

            //if (Math.Abs(objectiveCheck - solver.get_objective()) > 1e-11)
            //    return false;

            double cutValue = Math.Ceiling(total);
            if (cutValue - total < 1e-11)
                return false;

            Console.WriteLine($"Adding integer objective cut on {indexes.Count}/{colCount} columns, value {total} -> {cutValue}");
            var indexesArray = indexes.ToArray();
            var values = new double[indexesArray.Length];
            Array.Fill(values, 1.0);
            return solver.add_constraintex(indexesArray.Length, values, indexesArray, lpsolve_constr_types.GE,
                cutValue);
        }
    }
}