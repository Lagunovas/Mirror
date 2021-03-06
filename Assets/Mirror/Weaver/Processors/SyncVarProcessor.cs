// all the [SyncVar] code from NetworkBehaviourProcessor in one place
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Mirror.Weaver
{
    public static class SyncVarProcessor
    {
        const int k_SyncVarLimit = 64; // ulong = 64 bytes

        // returns false for error, not for no-hook-exists
        public static bool CheckForHookFunction(TypeDefinition td, FieldDefinition syncVar, out MethodDefinition foundMethod)
        {
            foundMethod = null;
            foreach (var ca in syncVar.CustomAttributes)
            {
                if (ca.AttributeType.FullName == Weaver.SyncVarType.FullName)
                {
                    foreach (CustomAttributeNamedArgument customField in ca.Fields)
                    {
                        if (customField.Name == "hook")
                        {
                            string hookFunctionName = customField.Argument.Value as string;

                            foreach (var m in td.Methods)
                            {
                                if (m.Name == hookFunctionName)
                                {
                                    if (m.Parameters.Count == 1)
                                    {
                                        if (m.Parameters[0].ParameterType != syncVar.FieldType)
                                        {
                                            Log.Error("SyncVar Hook function " + hookFunctionName + " has wrong type signature for " + td.Name);
                                            Weaver.fail = true;
                                            return false;
                                        }
                                        foundMethod = m;
                                        return true;
                                    }
                                    Log.Error("SyncVar Hook function " + hookFunctionName + " must have one argument " + td.Name);
                                    Weaver.fail = true;
                                    return false;
                                }
                            }
                            Log.Error("SyncVar Hook function " + hookFunctionName + " not found for " + td.Name);
                            Weaver.fail = true;
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        public static MethodDefinition ProcessSyncVarGet(FieldDefinition fd, string originalName)
        {
            //Create the get method
            MethodDefinition get = new MethodDefinition(
                    "get_Network" + originalName, MethodAttributes.Public |
                    MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig,
                    fd.FieldType);

            ILProcessor getWorker = get.Body.GetILProcessor();

            getWorker.Append(getWorker.Create(OpCodes.Ldarg_0));
            getWorker.Append(getWorker.Create(OpCodes.Ldfld, fd));
            getWorker.Append(getWorker.Create(OpCodes.Ret));

            get.Body.Variables.Add(new VariableDefinition(fd.FieldType));
            get.Body.InitLocals = true;
            get.SemanticsAttributes = MethodSemanticsAttributes.Getter;

            return get;
        }

        public static MethodDefinition ProcessSyncVarSet(TypeDefinition td, FieldDefinition fd, string originalName, long dirtyBit, FieldDefinition netFieldId)
        {
            //Create the set method
            MethodDefinition set = new MethodDefinition("set_Network" + originalName, MethodAttributes.Public |
                    MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig,
                    Weaver.voidType);

            ILProcessor setWorker = set.Body.GetILProcessor();

            // this
            setWorker.Append(setWorker.Create(OpCodes.Ldarg_0));

            // new value to set
            setWorker.Append(setWorker.Create(OpCodes.Ldarg_1));

            // reference to field to set
            setWorker.Append(setWorker.Create(OpCodes.Ldarg_0));
            setWorker.Append(setWorker.Create(OpCodes.Ldflda, fd));

            // dirty bit
            setWorker.Append(setWorker.Create(OpCodes.Ldc_I8, dirtyBit)); // 8 byte integer aka long

            MethodDefinition hookFunctionMethod;
            CheckForHookFunction(td, fd, out hookFunctionMethod);

            if (hookFunctionMethod != null)
            {
                //if (NetworkServer.localClientActive && !syncVarHookGuard)
                Instruction label = setWorker.Create(OpCodes.Nop);
                setWorker.Append(setWorker.Create(OpCodes.Call, Weaver.NetworkServerGetLocalClientActive));
                setWorker.Append(setWorker.Create(OpCodes.Brfalse, label));
                setWorker.Append(setWorker.Create(OpCodes.Ldarg_0));
                setWorker.Append(setWorker.Create(OpCodes.Call, Weaver.getSyncVarHookGuard));
                setWorker.Append(setWorker.Create(OpCodes.Brtrue, label));

                // syncVarHookGuard = true;
                setWorker.Append(setWorker.Create(OpCodes.Ldarg_0));
                setWorker.Append(setWorker.Create(OpCodes.Ldc_I4_1));
                setWorker.Append(setWorker.Create(OpCodes.Call, Weaver.setSyncVarHookGuard));

                // call hook
                setWorker.Append(setWorker.Create(OpCodes.Ldarg_0));
                setWorker.Append(setWorker.Create(OpCodes.Ldarg_1));
                setWorker.Append(setWorker.Create(OpCodes.Call, hookFunctionMethod));

                // syncVarHookGuard = false;
                setWorker.Append(setWorker.Create(OpCodes.Ldarg_0));
                setWorker.Append(setWorker.Create(OpCodes.Ldc_I4_0));
                setWorker.Append(setWorker.Create(OpCodes.Call, Weaver.setSyncVarHookGuard));

                setWorker.Append(label);
            }

            if (fd.FieldType.FullName == Weaver.gameObjectType.FullName)
            {
                // reference to netId Field to set
                setWorker.Append(setWorker.Create(OpCodes.Ldarg_0));
                setWorker.Append(setWorker.Create(OpCodes.Ldflda, netFieldId));

                setWorker.Append(setWorker.Create(OpCodes.Call, Weaver.setSyncVarGameObjectReference));
            }
            else
            {
                // make generic version of SetSyncVar with field type
                GenericInstanceMethod gm = new GenericInstanceMethod(Weaver.setSyncVarReference);
                gm.GenericArguments.Add(fd.FieldType);

                // invoke SetSyncVar
                setWorker.Append(setWorker.Create(OpCodes.Call, gm));
            }

            setWorker.Append(setWorker.Create(OpCodes.Ret));

            set.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.In, fd.FieldType));
            set.SemanticsAttributes = MethodSemanticsAttributes.Setter;

            return set;
        }

        public static void ProcessSyncVar(TypeDefinition td, FieldDefinition fd, List<FieldDefinition> syncVarNetIds, long dirtyBit)
        {
            string originalName = fd.Name;

            Weaver.DLog(td, "Sync Var " + fd.Name + " " + fd.FieldType + " " + Weaver.gameObjectType);

            // GameObject SyncVars have a new field for netId
            FieldDefinition netFieldId = null;
            if (fd.FieldType.FullName == Weaver.gameObjectType.FullName)
            {
                netFieldId = new FieldDefinition("___" + fd.Name + "NetId",
                    FieldAttributes.Private,
                    Weaver.uint32Type);

                syncVarNetIds.Add(netFieldId);
                Weaver.lists.netIdFields.Add(netFieldId);
            }

            var get = ProcessSyncVarGet(fd, originalName);
            var set = ProcessSyncVarSet(td, fd, originalName, dirtyBit, netFieldId);

            //NOTE: is property even needed? Could just use a setter function?
            //create the property
            PropertyDefinition propertyDefinition = new PropertyDefinition("Network" + originalName, PropertyAttributes.None, fd.FieldType)
            {
                GetMethod = get, SetMethod = set
            };

            //add the methods and property to the type.
            td.Methods.Add(get);
            td.Methods.Add(set);
            td.Properties.Add(propertyDefinition);
            Weaver.lists.replacementSetterProperties[fd] = set;
        }

        public static void ProcessSyncVars(TypeDefinition td, List<FieldDefinition> syncVars, List<FieldDefinition> syncObjects, List<FieldDefinition> syncVarNetIds)
        {
            int numSyncVars = 0;

            // the mapping of dirtybits to sync-vars is implicit in the order of the fields here. this order is recorded in m_replacementProperties.
            // start assigning syncvars at the place the base class stopped, if any
            int dirtyBitCounter = Weaver.GetSyncVarStart(td.BaseType.FullName);

            syncVarNetIds.Clear();

            // find syncvars
            foreach (FieldDefinition fd in td.Fields)
            {
                foreach (var ca in fd.CustomAttributes)
                {
                    if (ca.AttributeType.FullName == Weaver.SyncVarType.FullName)
                    {
                        var resolvedField = fd.FieldType.Resolve();

                        if (resolvedField.IsDerivedFrom(Weaver.NetworkBehaviourType))
                        {
                            Log.Error("SyncVar [" + fd.FullName + "] cannot be derived from NetworkBehaviour.");
                            Weaver.fail = true;
                            return;
                        }

                        if (resolvedField.IsDerivedFrom(Weaver.ScriptableObjectType))
                        {
                            Log.Error("SyncVar [" + fd.FullName + "] cannot be derived from ScriptableObject.");
                            Weaver.fail = true;
                            return;
                        }

                        if ((fd.Attributes & FieldAttributes.Static) != 0)
                        {
                            Log.Error("SyncVar [" + fd.FullName + "] cannot be static.");
                            Weaver.fail = true;
                            return;
                        }

                        if (resolvedField.HasGenericParameters)
                        {
                            Log.Error("SyncVar [" + fd.FullName + "] cannot have generic parameters.");
                            Weaver.fail = true;
                            return;
                        }

                        if (resolvedField.IsInterface)
                        {
                            Log.Error("SyncVar [" + fd.FullName + "] cannot be an interface.");
                            Weaver.fail = true;
                            return;
                        }

                        var fieldModuleName = resolvedField.Module.Name;
                        if (fieldModuleName != Weaver.scriptDef.MainModule.Name &&
                            fieldModuleName != Weaver.m_UnityAssemblyDefinition.MainModule.Name &&
                            fieldModuleName != Weaver.m_UNetAssemblyDefinition.MainModule.Name &&
                            fieldModuleName != Weaver.corLib.Name &&
                            fieldModuleName != "System.Runtime.dll" && // this is only for Metro, built-in types are not in corlib on metro
                            fieldModuleName != "netstandard.dll" // handle built-in types when weaving new C#7 compiler assemblies
                            )
                        {
                            Log.Error("SyncVar [" + fd.FullName + "] from " + resolvedField.Module.ToString() + " cannot be a different module.");
                            Weaver.fail = true;
                            return;
                        }

                        if (fd.FieldType.IsArray)
                        {
                            Log.Error("SyncVar [" + fd.FullName + "] cannot be an array. Use a SyncList instead.");
                            Weaver.fail = true;
                            return;
                        }

                        if (SyncObjectProcessor.ImplementsSyncObject(fd.FieldType))
                        {
                            Log.Warning(string.Format("Script class [{0}] has [SyncVar] attribute on SyncList field {1}, SyncLists should not be marked with SyncVar.", td.FullName, fd.Name));
                            break;
                        }

                        syncVars.Add(fd);

                        ProcessSyncVar(td, fd, syncVarNetIds, 1L << dirtyBitCounter);
                        dirtyBitCounter += 1;
                        numSyncVars += 1;

                        if (dirtyBitCounter == k_SyncVarLimit)
                        {
                            Log.Error("Script class [" + td.FullName + "] has too many SyncVars (" + k_SyncVarLimit + "). (This could include base classes)");
                            Weaver.fail = true;
                            return;
                        }
                        break;
                    }
                }

                if (fd.FieldType.FullName.Contains("Mirror.SyncListStruct"))
                {
                    Log.Error("SyncListStruct member variable [" + fd.FullName + "] must use a dervied class, like \"class MySyncList : SyncListStruct<MyStruct> {}\".");
                    Weaver.fail = true;
                    return;
                }

                if (fd.FieldType.Resolve().ImplementsInterface(Weaver.SyncObjectType))
                {
                    if (fd.IsStatic)
                    {
                        Log.Error("SyncList [" + td.FullName + ":" + fd.FullName + "] cannot be a static");
                        Weaver.fail = true;
                        return;
                    }

                    syncObjects.Add(fd);
                }
            }

            foreach (FieldDefinition fd in syncVarNetIds)
            {
                td.Fields.Add(fd);
            }

            Weaver.SetNumSyncVars(td.FullName, numSyncVars);
        }
    }
}