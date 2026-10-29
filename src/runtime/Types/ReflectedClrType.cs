using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

using static Python.Runtime.PythonException;

namespace Python.Runtime;

[Serializable]
internal sealed class ReflectedClrType : PyType
{
    private ReflectedClrType(StolenReference reference) : base(reference, prevalidated: true) { }
    internal ReflectedClrType(ReflectedClrType original) : base(original, prevalidated: true) { }
    internal ReflectedClrType(BorrowedReference original) : base(original) { }
    ReflectedClrType(SerializationInfo info, StreamingContext context) : base(info, context) { }

    internal ClassBase Impl => (ClassBase)ManagedType.GetManagedObject(this)!;

    /// <summary>
    /// Get the Python type that reflects the given CLR type.
    /// </summary>
    /// <remarks>
    /// Returned <see cref="ReflectedClrType"/> might be partially initialized.
    /// </remarks>
    public static ReflectedClrType GetOrCreate(Type type)
    {
        if (ClassManager.cache.TryGetValue(type, out var pyType))
        {
            return pyType;
        }

        // Ensure, that matching Python type exists first.
        // It is required for self-referential classes
        // (e.g. with members, that refer to the same class)
        pyType = AllocateClass(type);
        ClassManager.cache.Add(type, pyType);

        var impl = ClassManager.CreateClass(type);

        TypeManager.InitializeClassCore(type, pyType, impl);

        ClassManager.InitClassBase(type, impl, pyType);

        // Now we force initialize the Python type object to reflect the given
        // managed type, filling the Python type slots with thunks that
        // point to the managed methods providing the implementation.
        TypeManager.InitializeClass(pyType, impl, type);

        return pyType;
    }

    internal void Restore(Dictionary<string, object?> context)
    {
        var cb = (ClassBase)context["impl"]!;

        Debug.Assert(cb is not null);

        cb!.Load(this, context);

        Restore(cb);
    }

    internal void Restore(ClassBase cb)
    {
        ClassManager.InitClassBase(cb.type.Value, cb, this);

        TypeManager.InitializeClass(this, cb, cb.type.Value);
    }

    internal static NewReference CreateSubclass(ClassBase baseClass, IList<Type> interfaces,
                                                string name, string? assembly, string? ns,
                                                BorrowedReference dict, string? definitionSite = null)
    {
        try
        {
            Type subType = ClassDerivedObject.CreateDerivedType(name,
                baseClass.type.Value,
                interfaces,
                dict,
                ns,
                assembly,
                definitionSite: definitionSite);

            ClassManager.cache.Remove(subType);
            var py_type = GetOrCreate(subType);

            // by default the class dict will have all the C# methods in it, but as this is a
            // derived class we want the python overrides in there instead if they exist.
            var cls_dict = Util.ReadRef(py_type, TypeOffset.tp_dict);
            ThrowIfIsNotZero(Runtime.PyDict_Update(cls_dict, dict));
            // Update the __classcell__ if it exists
            BorrowedReference cell = Runtime.PyDict_GetItemString(cls_dict, "__classcell__");
            if (!cell.IsNull)
            {
                ThrowIfIsNotZero(Runtime.PyCell_Set(cell, py_type));
                ThrowIfIsNotZero(Runtime.PyDict_DelItemString(cls_dict, "__classcell__"));
            }
            WrapAsClassMethod(cls_dict, "__init_subclass__");
            AddInstanceDictDescriptor(py_type, cls_dict);
            Runtime.PyType_Modified(py_type);
            WrapAsClassMethod(cls_dict, "__class_getitem__");

            return new NewReference(py_type);
        }
        catch (Exception e)
        {
            return Exceptions.RaiseTypeError(e.Message);
        }
    }

    /// <summary>
    /// Makes a plain function stored under <paramref name="name"/> a classmethod, as
    /// <c>type.__new__</c> does for <c>__init_subclass__</c> and <c>__class_getitem__</c>.
    /// </summary>
    static void WrapAsClassMethod(BorrowedReference classDict, string name)
    {
        BorrowedReference member = Runtime.PyDict_GetItemString(classDict, name);
        if (member.IsNull)
        {
            return;
        }
        using var types = PyModule.Import("types");
        using var functionType = types.GetAttr("FunctionType");
        if (Runtime.PyObject_TYPE(member) != functionType.Reference)
        {
            return;
        }
        using var builtins = PyModule.Import("builtins");
        using var classMethod = builtins.GetAttr("classmethod");
        using var function = new PyObject(member);
        using var wrapped = classMethod.Invoke(function);
        ThrowIfIsNotZero(Runtime.PyDict_SetItemString(classDict, name, wrapped));
    }

    static IntPtr instanceDictGetSet;

    /// <summary>
    /// Exposes the instance dictionary as <c>__dict__</c>, which <c>type.__new__</c> would have
    /// added, so <c>vars()</c> and descriptors such as <c>functools.cached_property</c> work.
    /// </summary>
    static void AddInstanceDictDescriptor(PyType type, BorrowedReference classDict)
    {
        if (Util.ReadInt32(type, TypeOffset.tp_dictoffset) <= 0
            || !Runtime.PyDict_GetItemString(classDict, "__dict__").IsNull)
        {
            return;
        }
        if (instanceDictGetSet == IntPtr.Zero)
        {
            IntPtr getSet = Marshal.AllocHGlobal(5 * IntPtr.Size);
            Marshal.WriteIntPtr(getSet, 0, Marshal.StringToHGlobalAnsi("__dict__"));
            Marshal.WriteIntPtr(getSet, IntPtr.Size, Runtime.PyObject_GenericGetDictAddress);
            Marshal.WriteIntPtr(getSet, 2 * IntPtr.Size, Runtime.PyObject_GenericSetDictAddress);
            Marshal.WriteIntPtr(getSet, 3 * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(getSet, 4 * IntPtr.Size, IntPtr.Zero);
            instanceDictGetSet = getSet;
        }
        using var descriptor = Runtime.PyDescr_NewGetSet(type, instanceDictGetSet);
        ThrowIfIsNotZero(Runtime.PyDict_SetItemString(classDict, "__dict__", descriptor.BorrowOrThrow()));
    }

    static ReflectedClrType AllocateClass(Type clrType)
    {
        string name = TypeManager.GetPythonTypeName(clrType);

        var type = TypeManager.AllocateTypeObject(name, Runtime.PyCLRMetaType);
        type.Flags = TypeFlags.Default
                        | TypeFlags.HasClrInstance
                        | TypeFlags.HeapType
                        | TypeFlags.BaseType
                        | TypeFlags.HaveGC;

        return new ReflectedClrType(type.Steal());
    }

    public override bool Equals(PyObject? other) => rawPtr == other?.DangerousGetAddressOrNull();
    public override int GetHashCode() => rawPtr.GetHashCode();
}
