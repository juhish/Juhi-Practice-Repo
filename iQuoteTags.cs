using System;
using System.Text.RegularExpressions;
using System.Web;
using Homesite.ECommerce.Context;
using Homesite.ECommerce.Presentation.ServiceClients;

namespace Homesite.ECommerce.Presentation
{
    public class iQuoteTags
    {
        //private static Regex tagRegex = new Regex(".*?(%)((?:[a-z][a-z]+))(%)", RegexOptions.IgnoreCase);	
        private static Regex tagRegex = new Regex("(%)((?:[a-z][a-z0-9]+))(%)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Anywhere in the iQuote page that contains a tag in the format of:
        ///     %TAG_TYPE%
        /// Gets replaced with the corresponding Info() value from configuration,
        /// which is the partner info configuration section.
        /// </summary>
        public static string ProcessTags(string url, string input) 
        {
            UIService uiService = new UIService();
            
            QuoteHeader qHeader = (QuoteHeader)HttpContext.Current.Session["Header"];




            Match m;
            string tag;
            string replacementText = string.Empty;

            if (input.Contains("%"))
            {
                while (tagRegex.IsMatch(input))
                {

                    m = tagRegex.Matches(input)[0];
                    tag = m.Groups[2].ToString();

                    switch (tag)
                    {
                        case "PolicyNumber":
                            replacementText = "POLICY??";
                            break;
                        case "QuoteNumber":
                            replacementText = qHeader.QuoteNumber;
                            break;
                        case "ReferenceNumber":
                            if (!String.IsNullOrEmpty(qHeader.SessionId.ToString()))
                                replacementText = qHeader.SessionId.ToString();
                            else
                                replacementText = String.Empty;
                            break;
                        case "GoogleAnalyticsTracker":
                            replacementText = uiService.GetPartnerSetting("GoogleAnalyticsTracker");
                            break;
                        case "EffectiveDatePlus60":
                           
                                DateTime EffctiveDate = Convert.ToDateTime(qHeader.PolicyEffectiveDate);
                                EffctiveDate = EffctiveDate.AddDays(60);
                                replacementText = EffctiveDate.ToShortDateString();
                         
                            break;
                        case "CurrentDatePlus60":
                            DateTime currentDate = DateTime.Now;
                            currentDate = currentDate.AddDays(61);
                            replacementText = currentDate.ToShortDateString();
                            break;
                        case "ShortProductName":
                            if (qHeader.FormCode == 3) replacementText = "Home";
                            else if (qHeader.FormCode == 4) replacementText = "Renters";
                            else if (qHeader.FormCode == 6) replacementText = "Condo";
                            else throw new PresentationException(string.Format("Unknown FormCode {0} in ShortProductName substitution", qHeader.FormCode));
                            break;
                        case "WelcomeText":
                            if(qHeader.PartnerId.Equals(2319))
                            {
                                replacementText = uiService.GetWelcomeText();
                            }
                            else
                            {
                                replacementText = "";
                            }
                            break;
                        default:
                            replacementText = uiService.GetPartnerInfo(tag);
                            break;
                    }


                    try
                    {
                        input = tagRegex.Replace(input, replacementText, 1);
                    }
                    catch (ArgumentNullException ex)
                    {
                        throw new PresentationException("tag '" + tag + "' detected but replacement text was null... config files are messed up?" + ex.Message, ex.InnerException);
                    }
                }
            }


            return input;
        }
    }
}
