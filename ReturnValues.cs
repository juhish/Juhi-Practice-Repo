using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.Serialization;
using System.Data;

namespace Homesite.ECommerce.DAC
{
    enum ReturnValues
    {
        Ok = -1,
        Duplicates = -2,
        NoRows = -3
    }

    [DataContract(Name = "LookupProviderReturnData", Namespace = "http://homesite.com/IQuote/ConsumerServices/datacontracts")]
    public class LookupProviderReturnData
    {
        public LookupProviderReturnData()
        {
            this._dataTbl = null;
            this._defaultValue = string.Empty;
        }

        //public LookupProviderReturnData(DataTable dt, string keyColumn, string valColumn)
        //{
        //    this._defaultValue = string.Empty;
        //    this._dataDict = LoadFromDataTable(dt, keyColumn, valColumn);
        //}

        //private Dictionary<string, string> _dataDict;
        //[DataMember(Name = "DataDictionary")]
        //public DataTable DataDictionary
        //{
        //    get { return _dataDict; }
        //    set { _dataDict = value; }
        //}

        private DataTable _dataTbl;
        [DataMember(Name = "DataTbl")]
        public DataTable DataTbl
        {
            get { return _dataTbl; }
            set { _dataTbl = value; }
        }
        
        private string _defaultValue;
        [DataMember(Name = "DefaultValue")]
        public string DefaultValue
        {
            get { return _defaultValue; }
            set { _defaultValue = value; }
        }

        //private Dictionary<string, string> LoadFromDataTable(DataTable dt, string keyColumn, string valColumn)
        //{
        //    Dictionary<string, string> dict = new Dictionary<string, string>();
        //    foreach (DataRow dr in dt.Rows)
        //    {
        //        dict.Add(dr[keyColumn].ToString(), dr[valColumn].ToString()); 
        //    }
        //    return dict;
        //}

    }
}
