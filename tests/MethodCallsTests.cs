using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class MethodCallsTests
    {
        private static int s_counter;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Question(object value)
        {
            return value != null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Other()
        {
            return ++s_counter;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int AsksOnce(object value)
        {
            if (Question(value))
                return Other();
            switch (Other())
            {
                case 1: return 10;
                case 2: return 20;
                case 3: return 30;
                default: return (int)(Other() * 2.5);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int AsksTwice(object value)
        {
            var a = Question(value);
            var b = Question(a);
            long big = 1234567890123L;
            return a && b ? (int)(big % 7) : Other();
        }

        private static MethodInfo Method(string name)
        {
            return typeof(MethodCallsTests).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
        }

        [Fact]
        public void EveryCallToTheMethodIsCounted()
        {
            Assert.Equal(1, MethodCalls.Count(Method("AsksOnce"), Method("Question")));
            Assert.Equal(2, MethodCalls.Count(Method("AsksTwice"), Method("Question")));
        }

        [Fact]
        public void CallsToOtherMethodsAreNotCounted()
        {
            Assert.Equal(0, MethodCalls.Count(Method("Other"), Method("Question")));
            Assert.Equal(1, MethodCalls.Count(Method("AsksTwice"), Method("Other")));
        }

        [Fact]
        public void AMethodOfAnotherModuleCannotBeCounted()
        {
            Assert.Null(MethodCalls.Count(Method("AsksOnce"), typeof(string).GetMethod("IsNullOrEmpty")));
            Assert.Null(MethodCalls.Count(null, Method("Question")));
        }

        [Fact]
        public void ABodyThatCannotBeReadToTheEndIsNotCounted()
        {
            // A call opcode cut in the middle of its token.
            Assert.Null(MethodCalls.Count(new byte[] { 0x28, 0x01, 0x02 }, 0x0201));
            // An unknown two-byte instruction.
            Assert.Null(MethodCalls.Count(new byte[] { 0xFE, 0xFF }, 0));
            Assert.Null(MethodCalls.Count((byte[])null, 0));
        }

        [Fact]
        public void ARawCallIsRecognizedByItsToken()
        {
            var token = 0x0A000123;
            var bytes = BitConverter.GetBytes(token);
            var il = new byte[]
            {
                0x02,                                     // ldarg.0
                0x6F, bytes[0], bytes[1], bytes[2], bytes[3], // callvirt token
                0x2C, 0x00,                               // brfalse.s 0
                0x28, bytes[0], bytes[1], bytes[2], bytes[3], // call token
                0x2A,                                     // ret
            };
            Assert.Equal(2, MethodCalls.Count(il, token));
            Assert.Equal(0, MethodCalls.Count(il, token + 1));
        }
    }
}
