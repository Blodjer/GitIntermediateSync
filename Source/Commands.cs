using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;

namespace GitIntermediateSync
{
    class OperationInfo : Attribute
    {
        public Operation operation = Operation.Unkown;
        public string command = string.Empty;
        public string description = string.Empty;
        public bool showDestructiveWarning = true;
    }

    enum Operation
    {
        Unkown,

        [OperationInfo(command = "save", description = "Save and upload the current diff", showDestructiveWarning = false)]
        Save,
        [OperationInfo(command = "apply", description = "Apply the latest state to the local repository", showDestructiveWarning = false)]
        Apply,
        [OperationInfo(command = "compare", description = "Diff the difference between the working directory and the latest available patch", showDestructiveWarning = false)]
        Compare
    }

    public enum OperationResult
    {
        Unknown,
        Success,
        Failure,
        Abort
    }

    struct OperationReturn
    {
        public readonly OperationResult Result;

        private OperationReturn(in OperationResult result)
        {
            this.Result = result;
        }

        public static implicit operator OperationReturn(in OperationResult result)
        {
            return new OperationReturn(result);
        }

        public static implicit operator OperationReturn(in bool success)
        {
            return success ? OperationResult.Success : OperationResult.Failure;
        }

        public static implicit operator OperationResult(OperationReturn operationReturn)
        {
            return operationReturn.Result;
        }

        public static implicit operator bool(OperationReturn operationReturn)
        {
            return operationReturn.Result == OperationResult.Success;
        }
    }

    abstract class OperationCommands
    {
        private static readonly ReadOnlyDictionary<string, OperationInfo> m_commandToOperationMap;
        private static readonly ReadOnlyDictionary<Operation, OperationInfo> m_operationInfoMap;

        static OperationCommands()
        {
            var commandToOperationMap = new Dictionary<string, OperationInfo>();
            var operationInfoMap = new Dictionary<Operation, OperationInfo>();

            var operationEnumValues = Enum.GetValues(typeof(Operation));
            foreach (Operation operation in operationEnumValues)
            {
                var info = typeof(Operation).GetField(operation.ToString()).GetCustomAttribute<OperationInfo>();
                if (info == null)
                {
                    continue;
                }

                info.operation = operation;

                commandToOperationMap.Add(info.command, info);
                operationInfoMap.Add(operation, info);
            }

            m_commandToOperationMap = new ReadOnlyDictionary<string, OperationInfo>(commandToOperationMap);
            m_operationInfoMap = new ReadOnlyDictionary<Operation, OperationInfo>(operationInfoMap);
        }

        public static bool TryGetOperation(in string command, out OperationInfo operationInfo)
        {
            return m_commandToOperationMap.TryGetValue(command, out operationInfo);
        }

        public static void PrintAllCommands()
        {
            Console.Out.WriteLine("Available commands:");
            foreach (var pair in m_operationInfoMap)
            {
                OperationInfo info = pair.Value;
                Console.Out.WriteLine("\t{0}\t\t{1}", info.command, info.description);
            }
        }

        public static void PrintCommand(in Operation operation)
        {
            if (m_operationInfoMap.TryGetValue(operation, out OperationInfo info))
            {
                // TODO
                Console.Out.WriteLine(info.description);
            }
        }
    }
}
