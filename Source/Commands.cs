using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;

namespace GitIntermediateSync
{
    public class OperationInfo : Attribute
    {
        public string command = string.Empty;
        public bool critical = true;
        public string description = string.Empty;
    }

    public enum Operation
    {
        [OperationInfo(command = "save", critical = false, description = "Save and upload the current diff")]
        Save,
        [OperationInfo(command = "apply", critical = true, description = "Apply the latest state to the local repository")]
        Apply
    }

    class OperationCommands
    {
        static OperationCommands()
        {
            var d1 = new Dictionary<string, Operation>();
            var d2 = new Dictionary<Operation, OperationInfo>();

            var opValues = Enum.GetValues(typeof(Operation));
            foreach (Operation op in opValues)
            {
                var info = typeof(Operation).GetField(op.ToString()).GetCustomAttribute<OperationInfo>();
                d1.Add(info.command, op);
                d2.Add(op, info);
            }

            commandToOperationMap = new ReadOnlyDictionary<string, Operation>(d1);
            operationInfoMap = new ReadOnlyDictionary<Operation, OperationInfo>(d2);
        }

        private static ReadOnlyDictionary<string, Operation> commandToOperationMap;
        private static ReadOnlyDictionary<Operation, OperationInfo> operationInfoMap;

        public static bool TryGetOperation(in string command, out Operation operation, out OperationInfo operationInfo)
        {
            operationInfo = new OperationInfo();
            return commandToOperationMap.TryGetValue(command, out operation) && operationInfoMap.TryGetValue(operation, out operationInfo);
        }

        public static void PrintAllCommands()
        {
            Console.Out.WriteLine("Available commands:");
            foreach (var pair in operationInfoMap)
            {
                OperationInfo info = pair.Value;
                Console.Out.WriteLine("\t{0}\t\t{1}", info.command, info.description);
            }
        }

        public static void PrintCommand(in Operation operation)
        {
            if (operationInfoMap.TryGetValue(operation, out OperationInfo info))
            {
                // TODO
                Console.Out.WriteLine(info.description);
            }
        }
    }
}
