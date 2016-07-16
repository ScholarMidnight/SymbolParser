﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// TASK LIST:
// 1. Improve platform dependant types (__int32). Add flag to indicate platform dependant, and don't apply these during cross referencing.
// 2. Perhaps improve the implementation of unknown types.

namespace SymbolParser
{
    public class SymbolParser
    {
        public const string OUT_CLASS_FOLDER_WINDOWS = @"API\Windows";
        public const string OUT_CLASS_FOLDER_LINUX = @"API\Linux";
        public const string OUT_FUNCTION_FOLDER = @"Functions";

        public readonly string[] blackListedPatterns =
        {
            "sub_",
            "unknown_",
            "SEH",
            "iterator'",
            "keyed to'",
            "MS_",
            "Mem5",
            "vector deleting",
            "scalar deleting",
            "SJournalEntry",
            "SMstKeyEntry",
            "SMstNameEntry",
            "SRecord",
            "SSubNetProfile",
            "flex_unit",
            "file4close",
            "i4canCreate",
            "bad_alloc",
            "bad_cast",
            "bad_exception",
            "bad_typeid",
            "exception",
            "type_info",
            "..."
        };

        public readonly string[] whitelistedFreestandingPatterns =
        {
            "Exo",
            "Admin",
            "Create"
        };

        public SymbolParser()
        {
            List<string> clean = cleanFile(CommandLine.args.parse);
            List<ParsedClass> classes = getClasses(getParsedLines(clean));
            List<ParsedStruct> structs = getStructs(File.ReadAllLines(CommandLine.args.structs).ToList());

            addStructsToClasses(structs, classes);

            if (CommandLine.args.crossref != null)
            {
                List<ParsedClass> crossRefClasses = getClasses(getParsedLines(cleanFile(CommandLine.args.crossref)));
                crossReference(classes, crossRefClasses);
            }

            handleDependencies(classes);

            dumpStandaloneFiles(classes);
            dumpClassFiles(classes);

            if (CommandLine.args.produceUnityBuildFile)
            {
                dumpUnityBuildFile(classes);
            }
        }

        public static string preprocessTemplate(string line)
        {
            List<string> matching = SymbolParser.getMatchingBrackets(line);
            return matching.Aggregate(line, (current, match) => current.Replace(match, match.Replace(" ", "^")));
        }

        public static string handleTemplatedName(string templatedName)
        {
            return handleTemplatedName_r(templatedName).Item1;
        }

        public static List<string> getMatchingBrackets(string line)
        {
            var matchingBrackets = new List<string>();

            int index = line.IndexOf('<');

            while (index != -1)
            {
                ++index;
                var matchingIndex = 0;
                var count = 1;

                for (int i = index; i < line.Length; ++i)
                {
                    char at = line[i];

                    if (at == '<')
                    {
                        ++count;
                    }

                    if (at == '>')
                    {
                        --count;

                        if (count == 0)
                        {
                            matchingIndex = i;
                            break;
                        }
                    }
                }

                if (matchingIndex > index)
                {
                    matchingBrackets.Add(line.Substring(index, matchingIndex - index));
                    index = line.IndexOf('<', matchingIndex + 1);
                }
                else
                {
                    // There was no matching bracket -- probably because this is an operator.
                    break;
                }
            }

            return matchingBrackets;
        }

        private static Tuple<string, string> handleTemplatedName_r(string templatedName)
        {
            string originalName = templatedName;
            List<string> matching = getMatchingBrackets(templatedName);
            templatedName = matching.Select(handleTemplatedName_r)
                                    .Aggregate(templatedName, (current, recursiveMatch) => current
                                        .Replace("<" + recursiveMatch.Item2 + ">", "Templated" + recursiveMatch.Item1
                                        .Replace("*", "Ptr")
                                        .Replace("&", "Ref")
                                        .Replace("^", "")
                                        .Replace(",", "")));

            return new Tuple<string, string>(templatedName, originalName);
        }

        private void crossReference(List<ParsedClass> mainClasses, List<ParsedClass> secondClasses)
        {
            foreach (ParsedClass mainClass in mainClasses)
            {
                foreach (ParsedClass secondClass in secondClasses)
                {
                    if (mainClass.name == secondClass.name)
                    {
                        foreach (ParsedFunction mainFunc in mainClass.functions)
                        {
                            foreach (ParsedFunction secondFunc in secondClass.functions)
                            {
                                if (mainFunc.name == secondFunc.name)
                                {
                                    mainFunc.crossReferenceUsing(secondFunc);
                                }
                            }
                        }
                    }
                }
            }
        }

        private List<string> cleanFile(string path)
        {
            return cleanRaw(File.ReadAllLines(path).ToList());
        }

        // Provides processing on the provided file to make our life easier.
        private List<string> cleanRaw(List<string> lines)
        {
            List<string> cleanList = new List<string>();

            foreach(string line in lines)
            {
                string modifiedLine = line;

                if (modifiedLine[0] == '_' || blackListedPatterns.Any(modifiedLine.Contains))
                {
                    continue;
                }

                modifiedLine = modifiedLine.Replace("class ", "");
                modifiedLine = modifiedLine.Replace("struct ", "");
                modifiedLine = modifiedLine.Replace(" *", "*");
                modifiedLine = modifiedLine.Replace(" &", "&");
                modifiedLine = modifiedLine.Replace(")const", ") const");
                modifiedLine = CppType.convertArrayToPtr(modifiedLine);
                modifiedLine = CppType.convertEnumToInt(modifiedLine);
                cleanList.Add(modifiedLine);
            }

            return cleanList;
        }

        private List<ParsedLine> getParsedLines(List<string> lines)
        {
            return lines.AsParallel().AsOrdered().Select(line => new ParsedLine(line)).Where(parsedLine => parsedLine.functionName != null).ToList();
        }

        private List<ParsedClass> getClasses(List<ParsedLine> parsedLines)
        {
            var parsedClassDict = new Dictionary<string, ParsedClass>();
            var parsedFunctionDict = new Dictionary<ParsedClass, List<ParsedFunction>>();

            foreach (ParsedLine line in parsedLines)
            {
                if (line.className == null)
                {
                    continue;
                }

                ParsedClass thisClass = null;
                parsedClassDict.TryGetValue(handleTemplatedName(line.className), out thisClass);

                if (thisClass == null)
                {
                    thisClass = new ParsedClass(line);
                    parsedClassDict[thisClass.name] = thisClass;
                    parsedFunctionDict[thisClass] = new List<ParsedFunction>();
                }

                parsedFunctionDict[thisClass].Add(new ParsedFunction(line, thisClass));
            }

            List<ParsedClass> parsedClasses = parsedClassDict.Values.OrderBy(theClass => theClass.name).ToList();

            foreach (KeyValuePair<ParsedClass, List<ParsedFunction>> pair in parsedFunctionDict)
            {
                pair.Key.addFunctions(pair.Value);
            }

            return parsedClasses;
        }

        // Parses structs 
        private List<ParsedStruct> getStructs(List<string> lines)
        {
            List<ParsedStruct> structs = new List<ParsedStruct>();

            for (int i = 0; i < lines.Count; ++i)
            {
                // First, we're going to do a little preprocessing.
                // We want to delimit each string based on whitespace, then strip out anything weird.
                string[] blacklist =
                {
                    "__attribute__",
                    "__cppobj",
                    "[]"
                };

                lines[i] = string.Join(" ", lines[i].Split(' ').Where(str => !blacklist.Any(bl => str.Contains(bl)))).Replace("::", "__");
            }

            for (int i = 0; i < lines.Count; ++i)
            {
                string line = lines[i];

                if (!line.Contains("struct"))
                {
                    // Not something we care about.
                    continue;
                }

                if (line.Contains(";"))
                {
                    // Forward declaration -- ignore.
                    continue;
                }

                if (line.Contains("std__"))
                {
                    continue;
                }

                if (line.Contains("#define"))
                {
                    // Macro -- ignore.
                    continue;
                }

                if (line.Contains("typedef"))
                {
                    // Typedef -- ignore.
                    continue;
                }

                // Start of a struct definition
                List<string> linesInThisStruct = new List<string>();
                linesInThisStruct.Add(line);

                for (int j = i + 1; j < lines.Count; ++j, ++i)
                {
                    string nextLine = lines[j];
                    linesInThisStruct.Add(nextLine);

                    if (nextLine == "};")
                    {
                        // End of struct definition.
                        ++i;
                        break;
                    }
                }

                ParsedStruct newStruct = new ParsedStruct(linesInThisStruct);

                if (!String.IsNullOrWhiteSpace(newStruct.name))
                {
                    structs.Add(newStruct);
                }
            }

            // Hack -- ignore all structs that don't start with C.
            structs.RemoveAll(st => st.name[0] != 'C');
            return structs;
        }

        private void addStructsToClasses(List<ParsedStruct> structs, List<ParsedClass> classes)
        {
            List<ParsedClass> classesToAdd = new List<ParsedClass>();

            foreach (ParsedStruct theStruct in structs)
            {
                ParsedClass matchingClass = null;

                foreach (ParsedClass theClass in classes)
                {
                    if (theClass.name == theStruct.name)
                    {
                        matchingClass = theClass;
                        break;
                    }
                }

                List<NamedCppType> data = theStruct.members.Select(member => member.data).ToList();

                if (matchingClass == null)
                {
                    ParsedClass newParsedClass = new ParsedClass(theStruct.name);
                    newParsedClass.addData(data);
                    classesToAdd.Add(newParsedClass);
                }
                else
                {
                    matchingClass.addData(data);
                }
            }

            classes.AddRange(classesToAdd);
            classes = classes.OrderBy(theClass => theClass.name).ToList();

            foreach (ParsedStruct theStruct in structs)
            {
                ParsedClass matchingClass = null;

                foreach (ParsedClass theClass in classes)
                {
                    if (theClass.name == theStruct.name)
                    {
                        matchingClass = theClass;
                        break;
                    }
                }

                Debug.Assert(matchingClass != null);

                foreach (string inh in theStruct.inheritsFrom)
                {
                    ParsedClass inheritsFromClass = null;

                    foreach (ParsedClass theClass in classes)
                    {
                        if (theClass.name == inh)
                        {
                            inheritsFromClass = theClass;
                            break;
                        }
                    }

                    Debug.Assert(inheritsFromClass != null);

                    if (matchingClass != null)
                    {
                        matchingClass.inherits.Add(inheritsFromClass);
                    }
                }
            }
        }

        private void handleDependencies(List<ParsedClass> classes)
        {
            // Determine dependencies
            foreach (ParsedClass theClass in classes)
            {
                var dependencies = new List<CppType>();

                foreach (ParsedClass inheritsFrom in theClass.inherits)
                {
                    dependencies.Add(new CppType(inheritsFrom.name));
                }

                foreach (ParsedFunction theFunction in theClass.functions)
                {
                    if (theFunction.returnType != null && !theFunction.returnType.isBaseType)
                    {
                        dependencies.Add(theFunction.returnType);
                    }

                    dependencies.AddRange(theFunction.parameters.Where(param => !param.isBaseType));
                }

                foreach (NamedCppType theData in theClass.data)
                {
                    if (!theData.type.isBaseType)
                    {
                        dependencies.Add(theData.type);
                    }
                }

                // Sorting the list beforehand ensures we don't have to check for duplicates every time.
                dependencies.Sort((first, second) => String.CompareOrdinal(first.type, second.type));

                var needConcreteDef = false;

                for (var i = 0; i < dependencies.Count; ++i)
                {
                    CppType dependency = dependencies[i];

                    // We can't really do anything with this.
                    if (dependency.type == "..." || dependency.type == theClass.name)
                    {
                        continue;
                    }

                    if (!dependency.isPointer)
                    {
                        needConcreteDef = true;
                    }

                    if (dependencies.Count <= i + 1 || dependency.type != dependencies[i + 1].type)
                    {
                        ParsedClass dependencyClass = classes.FirstOrDefault(param => param.name == dependency.type);

                        if (dependencyClass != null)
                        {
                            if (needConcreteDef)
                            {
                                theClass.headerDependencies.Add(dependencyClass);
                            }
                            else
                            {

                                theClass.sourceDependencies.Add(dependencyClass);
                            }
                        }
                        else
                        {
                            theClass.unknownDependencies.Add(dependency);
                        }

                        needConcreteDef = false;
                    }
                }
            }
        }      

        private static void dumpStandaloneFiles(List<ParsedClass> classes)
        {
            string funcDir = Path.Combine(CommandLine.args.outDir, OUT_FUNCTION_FOLDER);

            if (!Directory.Exists(funcDir))
            {
                Directory.CreateDirectory(funcDir);
            }

            string fileName = CommandLine.args.target == CommandLineArgs.WINDOWS ? "FunctionsWindows" : "FunctionsLinux";
            var header = new List<string>();

            header.Add("#pragma once");
            header.Add("");
            header.Add("#include <cstdint>");
            header.Add("");
            header.Add("namespace " + CommandLine.args.libNamespace + " {");
            header.Add("");
            header.Add("namespace " + CommandLine.args.functionNamespace + " {");
            header.Add("");

            foreach (ParsedClass theClass in classes.Where(theClass => theClass.functions.Count != 0))
            {
                header.AddRange(theClass.asHeader());
                header.Add("");
            }

            header.Add("}");
            header.Add("");
            header.Add("}");

            File.WriteAllLines(Path.Combine(funcDir, fileName + ".hpp"), header);
        }

        private void dumpClassFiles(List<ParsedClass> classes)
        {
            string classDir = CommandLine.args.outDir;

            if (CommandLine.args.target == CommandLineArgs.WINDOWS)
            {
                classDir = Path.Combine(classDir, OUT_CLASS_FOLDER_WINDOWS);
            }
            else
            {
                classDir = Path.Combine(classDir, OUT_CLASS_FOLDER_LINUX);
            }

            if (!Directory.Exists(classDir))
            {
                Directory.CreateDirectory(classDir);
            }

            var source = new List<string>();
            var header = new List<string>();
            var unknownTypes = new List<CppType>();

            foreach (ParsedClass theClass in classes)
            {
                foreach (CppType unknownType in theClass.unknownDependencies.Where(unknownType => !unknownTypes.Contains(unknownType)))
                {
                    unknownTypes.Add(unknownType);
                }

                source.Clear();
                header.Clear();

                buildClassHeader(header, theClass);
                buildClassSource(source, theClass);

                File.WriteAllLines(Path.Combine(classDir, theClass.name + ".hpp"), header);
                File.WriteAllLines(Path.Combine(classDir, theClass.name + ".cpp"), source);
            }

            foreach (CppType unknownType in unknownTypes)
            {
                var headerFile = new List<string>();

                headerFile.Add("#pragma once");
                headerFile.Add("");
                headerFile.Add("namespace " + CommandLine.args.libNamespace + " {");
                headerFile.Add("");
                headerFile.Add("namespace " + CommandLine.args.classNamespace + " {");
                headerFile.Add("");
                headerFile.Add("struct " + unknownType.type + " { };");
                headerFile.Add("");
                headerFile.Add("}");
                headerFile.Add("");
                headerFile.Add("}");

                File.WriteAllLines(Path.Combine(classDir, "unknown_" + unknownType.type + ".hpp"), headerFile);
            }
        }

        private void dumpUnityBuildFile(List<ParsedClass> classes)
        {
            string classDir = CommandLine.args.outDir;

            if (CommandLine.args.target == CommandLineArgs.WINDOWS)
            {
                classDir = Path.Combine(classDir, OUT_CLASS_FOLDER_WINDOWS);
            }
            else
            {
                classDir = Path.Combine(classDir, OUT_CLASS_FOLDER_LINUX);
            }

            if (!Directory.Exists(classDir))
            {
                Directory.CreateDirectory(classDir);
            }

            List<string> source = new List<string>();

            foreach (ParsedClass parsedClass in classes)
            {
                source.Add("#include \"" + parsedClass.name + ".cpp\"");
            }

            File.WriteAllLines(Path.Combine(classDir, "UnityBuild.cpp"), source);
        }

        private static void buildClassSource(List<string> body, ParsedClass theClass)
        {
            body.Add("#include \"" + theClass.name + ".hpp\"");
            body.Add("#include \"Functions.hpp\"");
            body.Add("");

            if (theClass.sourceDependencies.Count > 0)
            {
                body.AddRange(theClass.sourceDependencies.Select(dependency => String.Format("#include \"{0}.hpp\"", dependency.name)));
                body.Add("");
            }

            body.Add("namespace " + CommandLine.args.libNamespace + " {");
            body.Add("");
            body.Add("namespace " + CommandLine.args.classNamespace + " {");
            body.Add("");
            body.AddRange(theClass.asClassSource());
            body.Add("}");
            body.Add("");
            body.Add("}");
        }

        private static void buildClassHeader(List<string> header, ParsedClass theClass)
        {
            header.Add("#pragma once");
            header.Add("");
            header.Add("#include <cstdint>");
            header.Add("");
            header.AddRange(theClass.headerDependencies.Select(dependency => String.Format("#include \"{0}.hpp\"", dependency.name)));
            header.AddRange(theClass.unknownDependencies.Select(dependency => String.Format("#include \"unknown_{0}.hpp\"", dependency.type)));

            if (theClass.headerDependencies.Count > 0 || theClass.unknownDependencies.Count > 0)
            {
                header.Add("");
            }

            header.Add("namespace " + CommandLine.args.libNamespace + " {");
            header.Add("");
            header.Add("namespace " + CommandLine.args.classNamespace + " {");
            header.Add("");

            if (theClass.sourceDependencies.Count > 0)
            {
                header.Add("// Forward class declarations (defined in the source file)");
                header.AddRange(theClass.sourceDependencies.Select(dependency => String.Format("class {0};", dependency.name)));
                header.Add("");
            }

            header.AddRange(theClass.asClassHeader());
            header.Add("}");
            header.Add("");
            header.Add("}");
        }
    }
}