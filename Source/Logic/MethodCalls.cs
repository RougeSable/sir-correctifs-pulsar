using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SirCorrectifsPulsar
{
    // Reads the instructions of a compiled method, to count its calls to
    // another method before patching it. Only the method body is read, with
    // the runtime's own table of instructions: nothing is generated, on any
    // runtime.
    public static class MethodCalls
    {
        private static readonly Dictionary<short, OpCode> Instructions = ReadInstructionTable();

        private static Dictionary<short, OpCode> ReadInstructionTable()
        {
            var table = new Dictionary<short, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode))
                    continue;
                var code = (OpCode)field.GetValue(null);
                table[code.Value] = code;
            }
            return table;
        }

        // How many call or callvirt instructions of the method body name the
        // given metadata token. Null when the body cannot be read to the end
        // (an instruction the table does not know).
        public static int? Count(byte[] il, int token)
        {
            if (il == null)
                return null;

            var count = 0;
            var position = 0;
            while (position < il.Length)
            {
                short value = il[position++];
                if (value == 0xFE)
                {
                    if (position >= il.Length)
                        return null;
                    value = (short)(0xFE00 | il[position++]);
                }

                OpCode code;
                if (!Instructions.TryGetValue(value, out code))
                    return null;

                var size = OperandSize(code.OperandType, il, position);
                if (size < 0 || position + size > il.Length)
                    return null;

                if ((code.Value == OpCodes.Call.Value || code.Value == OpCodes.Callvirt.Value) && BitConverter.ToInt32(il, position) == token)
                    count++;

                position += size;
            }
            return count;
        }

        // The calls of a compiled method to another method of the same module.
        public static int? Count(MethodBase method, MethodBase called)
        {
            if (method == null || called == null || method.Module != called.Module)
                return null;
            var body = method.GetMethodBody();
            return body == null ? null : Count(body.GetILAsByteArray(), called.MetadataToken);
        }

        private static int OperandSize(OperandType type, byte[] il, int position)
        {
            switch (type)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    if (position + 4 > il.Length)
                        return -1;
                    var targets = BitConverter.ToInt32(il, position);
                    if (targets < 0 || targets > (il.Length - position) / 4)
                        return -1;
                    return 4 + 4 * targets;
                default:
                    return 4;
            }
        }
    }
}
