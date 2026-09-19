using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Novgov.Network
{
    /// <summary>
    /// Utilitaire pour exécuter des coroutines en s'assurant que TOUTE exception,
    /// même levée dans un IEnumerator imbriqué, est correctement attrapée.
    /// Résout le bug du blocage "matchInProgress=true" lié au `yield return inner.Current;`.
    /// </summary>
    public static class SafeCoroutineRunner
    {
        public static IEnumerator Run(IEnumerator coroutine, Action onComplete, Action<Exception> onException)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(coroutine);

            while (stack.Count > 0)
            {
                IEnumerator current = stack.Peek();
                bool moved = false;
                try
                {
                    moved = current.MoveNext();
                }
                catch (Exception e)
                {
                    onException?.Invoke(e);
                    yield break;
                }

                if (moved)
                {
                    if (current.Current is IEnumerator nested)
                    {
                        stack.Push(nested);
                    }
                    else
                    {
                        yield return current.Current;
                    }
                }
                else
                {
                    stack.Pop();
                }
            }

            onComplete?.Invoke();
        }
    }
}
