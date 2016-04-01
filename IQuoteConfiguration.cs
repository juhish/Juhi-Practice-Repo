using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Text;
using System.ServiceModel.Channels;
using Homesite.ECommerce.Configuration.Cache;


[assembly: CLSCompliant(true)]
namespace Homesite.ECommerce.Configuration
{

    public class IQuoteConfiguration
    {

        Configuration configData = null;

        public static IQuoteConfiguration GetIQuoteCOnfigurationFromOperationContext()
        {
            RemoteEndpointMessageProperty mp = ConfigurationCache.GetRemoteEndpointMessageProperty();
            return ConfigurationCache.StaticCollection.GetConfiguration(mp);
        }
        public static IQuoteConfiguration GetIQuoteCOnfigurationFromPartnerID(int partnerID)
        {
            return new IQuoteConfiguration(partnerID);
        }

        public IQuoteConfiguration()
        {
            configData = new Configuration();
        }
        public IQuoteConfiguration(string url)
            : this()
        {
            GetDataFromRoutingTableByUrl(url);
            LoadConfigurationFile();
        }
        public IQuoteConfiguration(int partnerId)
            : this()
        {
            GetDataFromRoutingTableByPartnerId(partnerId);
            LoadConfigurationFile();
        }
        public IQuoteConfiguration(Uri url)
            : this(url.ToString())
        {
        }

        public int PartnerId
        {
            get
            {
                return configData.PartnerId;
            }
        }
        public int CallCenterId
        {
            get
            {
                var query =
                    from el in configData.ConfigurationData.Descendants("Ids").Elements()
                    where (int)el.Attribute("PartnerId") == configData.PartnerId
                    select el.Attribute("CallCenterId").Value;

                return Convert.ToInt16(query.First(), CultureInfo.InvariantCulture);

            }
        }
        public int EStaraImageId()
        {

            var query =
                from el in configData.ConfigurationData.Descendants("eStara").Elements()
                where (int)el.Attribute("PartnerId") == configData.PartnerId
                select el.Attribute("Id").Value;

            return Convert.ToInt16(query.First(), CultureInfo.InvariantCulture);

        }

        public string PartnerName
        {
            get
            {
                return GetPartnerInfoValue("Name");
            }
        }
        public string MasterPage
        {
            get
            {
                return GetPartnerSkinValue("MasterPage");
            }
        }
        public string StyleSheet
        {
            get
            {
                return GetPartnerSkinValue("Stylesheet"); ;
            }
        }
        public string AccountNumber
        {
            get
            {
                var query =
                    from e1 in configData.ConfigurationData.Descendants("AccountNumbers").Elements()
                    where (int)e1.Attribute("PartnerId") == configData.PartnerId
                    select e1.Value;

                return query.First();
            }
        }
        public string AMFNumber
        {
            get
            {
                var query =
                    from el in configData.ConfigurationData.Descendants("AccountNumbers").Elements()
                    where (int)el.Attribute("PartnerId") == configData.PartnerId
                    select el.Attribute("AMF").Value;

                return query.First();
            }
        }
        public string LevelNumber(int level)
        {
            var query = from e1 in configData.ConfigurationData.Descendants("LevelNumbers").Elements()
                        where (int)e1.Attribute("Level") == level &&
                              (int)e1.Attribute("PartnerId") == configData.PartnerId
                        select e1.Value;

            return query.First();
        }
        public string Info(string key)
        {
            return GetPartnerInfoValue(key);
        }
        public string Setting(string key)
        {
            return GetPartnerSettingValue(key);
        }
        public string PolicyDisclosure(string FormCode)
        {
            var query =
                from el in configData.ConfigurationData.Descendants("PolicyDisclosure").Elements()
                where el.Attribute("Form").Value.Contains(FormCode)
                select el.Value;

            return query.First();
        }
        public string EStaraImageData(string key)
        {
            var query =
                from el in configData.ConfigurationData.Descendants("eStara").Elements()
                where (int)el.Attribute("PartnerId") == configData.PartnerId
                select el.Element(key);

            return query.First().Value.ToString();

        }
        public string GetPartnerControl(string step)
        {
            string results = String.Empty;
            var query =
                from e1 in configData.ConfigurationData.Elements("PartnerControls")
                select e1.Element(step);

            if (query.Count() >= 1)
                results = (string)query.First();

            return results;
        }
        public string ProjectsEnabled()
        {
            var query =
                from el in configData.ConfigurationData.Descendants("Versioning").Elements()
                where (bool)Convert.ToBoolean(el.Value) == true
                select el;


            StringBuilder retv = new StringBuilder();

            foreach (XElement el in query)
            {
                string projectName = el.Attribute("Name").Value.ToString();

                retv.Append(projectName);
                retv.Append("|");
            }

            return retv.ToString();

        }


        public bool CheckForStateRedirect(string stateAbbreviation, int formCode)
        {

            System.Diagnostics.Debug.Assert(stateAbbreviation.Length == 2, "State Abbreviation should be two characters...");
            System.Diagnostics.Debug.Assert((formCode > 2 && formCode < 7), "Invalid Form Code");

            var query =
                from el in configData.ConfigurationData.Descendants("StateRedirects").Elements("StateRedirect")
                where (el.Attribute("State").Value.Contains(stateAbbreviation)  &&
                       el.Attribute("FormCode").Value.Contains(Convert.ToString(formCode)))
                select el.Value;

            if (query != null && query.Count() != 0)
                return true;
            else
                return false;
        }

        public bool CheckForZipcodeRedirect(string zipCode, int formCode)
        {

            System.Diagnostics.Debug.Assert(zipCode.Length == 5, "Zip Code should be five characters...");
            System.Diagnostics.Debug.Assert((formCode > 2 && formCode < 7), "Invalid Form Code");

            var query =
                from el in configData.ConfigurationData.Descendants("StateRedirects").Elements("ZipRedirect")
                where (el.Attribute("ZipCode").Value.Contains(zipCode) &&
                       el.Attribute("FormCode").Value.Contains(Convert.ToString(formCode)))
                select el.Value;

            if (query.Count() != 0)
                return true;
            else
                return false;
        }
        
        private void LoadConfigurationFile()
        {
            configData.ConfigurationData = ConfigurationCache.StaticCollection.GetConfigurationData(configData.ConfigurationFile);
        }

        private void GetDataFromRoutingTableByUrl(string url)
        {
            configData.RoutingTable = ConfigurationCache.StaticCollection.GetConfigurationRoutingData();

            var partnerId =
                from e1 in configData.RoutingTable.Elements("UrlRegex")
                select e1;

            foreach (XElement element in partnerId)
            {
                Regex re = new Regex(element.Value);
                if (re.IsMatch(url))
                {
                    configData.PartnerId = (int)Convert.ToInt16(element.Attribute("PartnerId").Value, CultureInfo.InvariantCulture);
                    configData.ConfigurationFile = element.Attribute("ConfigFile").Value.ToString();
                    break;
                }
            }

        }
        private void GetDataFromRoutingTableByPartnerId(int partnerId)
        {
            configData.RoutingTable = ConfigurationCache.StaticCollection.GetConfigurationRoutingData();

            var query =
                from el in configData.RoutingTable.Elements("UrlRegex")
                where (int)el.Attribute("PartnerId") == partnerId
                select el;

            configData.PartnerId = partnerId;
            configData.ConfigurationFile = query.First().Attribute("ConfigFile").Value.ToString();

        }

        public bool FunctionalityEnabled(string functionName)
        {
            var query =
                (
                    from el in configData.ConfigurationData.Elements("Functionality")
                    select el.Element(functionName)
                );

            return (bool)Convert.ToBoolean(query.First().Value, CultureInfo.InvariantCulture);
        }
        public bool StateEnabled(string stateCode)
        {
            var query =
                from el in configData.ConfigurationData.Descendants("StatesEnabled").Elements()
                where (int)el.Attribute("PartnerId") == configData.PartnerId &&
                      (string)el.Name.ToString() == stateCode
                select el.Value;

            bool retVal = false;

            if (query.Count() == 1)
                retVal = Convert.ToBoolean(query.First(), CultureInfo.InvariantCulture);

            return retVal;
        }
        private string GetPartnerInfoValue(string partnerInfoEntryName)
        {
            string partnerName = (string)
                (from e1 in configData.ConfigurationData.Elements("Info")
                 select e1.Element(partnerInfoEntryName)).First();
            return partnerName;
        }
        private string GetPartnerSkinValue(string partnerSkinEntryName)
        {
            string partnerName = (string)
                (from e1 in configData.ConfigurationData.Elements("Skin")
                 select e1.Element(partnerSkinEntryName)).First();
            return partnerName;
        }
        private string GetPartnerSettingValue(string partnerSettingEntryName)
        {
            string partnerName = (string)
                (from e1 in configData.ConfigurationData.Elements("Settings")
                 select e1.Element(partnerSettingEntryName)).First();
            return partnerName;
        }

    }
}
