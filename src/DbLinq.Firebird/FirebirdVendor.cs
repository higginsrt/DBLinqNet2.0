#region MIT license
// 
// MIT license
//
// Copyright (c) 2007-2008 Jiri Moudry, Pascal Craponne
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
// 
#endregion
using System;
using System.Data.Common;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Data.Linq.Mapping;
using System.Reflection;
using System.Data;

#if MONO_STRICT
using System.Data.Linq;
#else
using DbLinq.Data.Linq;
#endif

using DbLinq.Data.Linq.SqlClient;
using DbLinq.Util;
using DbLinq.Vendor;

namespace DbLinq.Firebird
{
    [Vendor(typeof(FirebirdProvider))]
#if !MONO_STRICT
    public
#endif
    class FirebirdVendor : Vendor.Implementation.Vendor
    {
        public override string VendorName { get { return "FirebirdSql"; } }

        protected readonly FirebirdSqlProvider sqlProvider = new FirebirdSqlProvider();
        public override ISqlProvider SqlProvider { get { return sqlProvider; } }

        /// <summary>
        /// call mysql stored proc or stored function, 
        /// optionally return DataSet, and collect return params.
        /// </summary>
        public override IExecuteResult ExecuteMethodCall(DataContext context, MethodInfo method
                                                                 , params object[] inputValues)
        {
            if (method == null)
                throw new ArgumentNullException("L56 Null 'method' parameter");

            //check to make sure there is exactly one [FunctionEx]? that's below.
            //FunctionAttribute functionAttrib = GetFunctionAttribute(method);
            var functionAttrib = context.Mapping.GetFunction(method);

            ParameterInfo[] paramInfos = method.GetParameters();
            //int numRequiredParams = paramInfos.Count(p => p.IsIn || p.IsRetval);
            //if (numRequiredParams != inputValues.Length)
            //    throw new ArgumentException("L161 Argument count mismatch");

            string sp_name = functionAttrib.MappedName;

            // picrap: is there any way to abstract some part of this?
            using (IDbCommand command = context.Connection.CreateCommand())
            {
                command.CommandText = sp_name;
                //FbSqlCommand command = new FbSqlCommand("select * from hello0()");
                int currInputIndex = 0;

                List<string> paramNames = new List<string>();
                for (int i = 0; i < paramInfos.Length; i++)
                {
                    ParameterInfo paramInfo = paramInfos[i];

                    //TODO: check to make sure there is exactly one [Parameter]?
                    ParameterAttribute paramAttrib = paramInfo.GetCustomAttributes(false).OfType<ParameterAttribute>().Single();

                    string paramName = "@" + paramAttrib.Name; //eg. '@param1'
                    paramNames.Add(paramName);

                    System.Data.ParameterDirection direction = GetDirection(paramInfo, paramAttrib);
                    //FbDbType dbType = FbSqlTypeConversions.ParseType(paramAttrib.DbType);
                    IDbDataParameter cmdParam = command.CreateParameter();
                    cmdParam.ParameterName = paramName;
                    //cmdParam.Direction = System.Data.ParameterDirection.Input;
                    if (direction == System.Data.ParameterDirection.Input || direction == System.Data.ParameterDirection.InputOutput)
                    {
                        object inputValue = inputValues[currInputIndex++];
                        cmdParam.Value = inputValue;
                    }
                    else
                    {
                        cmdParam.Value = null;
                    }
                    cmdParam.Direction = direction;
                    command.Parameters.Add(cmdParam);
                }

                if (!functionAttrib.IsComposable) // IsCompsable is false when we have a procedure
                {
                    //procedures: under the hood, this seems to prepend 'CALL '
                    command.CommandType = System.Data.CommandType.StoredProcedure;
                }
                else
                {
                    //functions: 'SELECT * FROM myFunction()' or 'SELECT * FROM hello(?s)'
                    command.CommandText = "SELECT * FROM " + command.CommandText + "("
                        + string.Join(",", paramNames.ToArray()) + ")";
                }

                if (method.ReturnType == typeof(DataSet))
                {
                    //unknown shape of resultset:
                    System.Data.DataSet dataSet = new DataSet();
                    //IDataAdapter adapter = new FbDataAdapter((FbCommand)command);
                    IDbDataAdapter adapter = CreateDataAdapter(context);
                    adapter.SelectCommand = command;
                    adapter.Fill(dataSet);
                    List<object> outParamValues = CopyOutParams(paramInfos, command.Parameters);
                    return new ProcedureResult(dataSet, outParamValues.ToArray());
                }
                else
                {
                    object obj = command.ExecuteScalar();
                    List<object> outParamValues = CopyOutParams(paramInfos, command.Parameters);
                    return new ProcedureResult(obj, outParamValues.ToArray());
                }
            }
        }

        static System.Data.ParameterDirection GetDirection(ParameterInfo paramInfo, ParameterAttribute paramAttrib)
        {
            //strange hack to determine what's a ref, out parameter:
            //http://lists.ximian.com/pipermain/mono-list/2003-March/012751.html
            bool hasAmpersand = paramInfo.ParameterType.FullName.Contains('&');
            if (paramInfo.IsOut)
                return System.Data.ParameterDirection.Output;
            if (hasAmpersand)
                return System.Data.ParameterDirection.InputOutput;
            return System.Data.ParameterDirection.Input;
        }

        /// <summary>
        /// Collect all Out or InOut param values, casting them to the correct .net type.
        /// </summary>
        private List<object> CopyOutParams(ParameterInfo[] paramInfos, IDataParameterCollection paramSet)
        {
            List<object> outParamValues = new List<object>();
            //Type type_t = typeof(T);
            int i = -1;
            foreach (IDbDataParameter param in paramSet)
            {
                i++;
                if (param.Direction == System.Data.ParameterDirection.Input)
                {
                    outParamValues.Add("unused");
                    continue;
                }

                object val = param.Value;
                Type desired_type = paramInfos[i].ParameterType;

                if (desired_type.Name.EndsWith("&"))
                {
                    //for ref and out parameters, we need to tweak ref types, e.g.
                    // "System.Int32&, mscorlib, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
                    string fullName1 = desired_type.AssemblyQualifiedName;
                    string fullName2 = fullName1.Replace("&", "");
                    desired_type = Type.GetType(fullName2);
                }
                try
                {
                    //fi.SetValue(t, val); //fails with 'System.Decimal cannot be converted to Int32'
                    //DbLinq.util.FieldUtils.SetObjectIdField(t, fi, val);
                    //object val2 = FieldUtils.CastValue(val, desired_type);
                    object val2 = TypeConvert.To(val, desired_type);
                    outParamValues.Add(val2);
                }
                catch (Exception)
                {
                    //fails with 'System.Decimal cannot be converted to Int32'
                    //Logger.Write(Level.Error, "CopyOutParams ERROR L245: failed on CastValue(): " + ex.Message);
                }
            }
            return outParamValues;
        }
    }
}
