using System;

namespace SledCoopMod
{
    // Marker for Harmony patch classes whose target types live in lazily-loaded
    // assemblies (PlayEveryWare.EpicOnlineServices, EOSSDK*, FishyEOS, sample
    // assemblies).  These classes are skipped during the initial PatchAll pass
    // in Plugin.Load and re-tried by EosLatePatcher whenever a new assembly
    // loads at runtime.
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class SledCoopEosPatchAttribute : Attribute { }
}
