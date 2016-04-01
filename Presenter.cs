using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Configuration;
using System.Web.UI;
using System.Web.UI.HtmlControls;
using Homesite.Diagnostics.ECommerce;
using Homesite.ECommerce.Context;
using Homesite.ECommerce.Presentation.Cache;
using Homesite.ECommerce.Presentation.LogPublisher;
using Homesite.ECommerce.Presentation.Presenters;
using Homesite.ECommerce.Presentation.ServiceClients;
using Homesite.ECommerce.RuleEngine;

namespace Homesite.ECommerce.Presentation
{
    public abstract class Presenter<TView>
        where TView : class
    {
        private const string JsStart = "<script language='javascript' type='text/javascript'>//<![CDATA[ ";
        private const string JsEnd = " //]]></script>";
        private const string GenericBaseStylesheetName = "base.css";
        private const string GenericPGRBaseStylesheetName = "progressive.css";
        private const string StylesheetSubfolder = "/Stylesheets/";




        private readonly Dictionary<string, object> _clientData;
        private bool _persistDataObjectRendered;
        private Hashtable _queryStringArray = new Hashtable();
        private QuoteService _quoteservice;

        protected Presenter()
        {
            _clientData = new Dictionary<string, object>();
        }

        protected QuoteService QuoteService
        {
            get { return _quoteservice ?? (_quoteservice = new QuoteService()); }
        }

        public TView View { get; set; }

        protected Hashtable QueryStringNameValueArray
        {
            get { return _queryStringArray; }
        }

        protected internal string CurrentPage
        {
            get { return GetCurrentPageView().Request.ServerVariables["SCRIPT_NAME"]; }
        }

        private static Dictionary<string, Dictionary<string, int>> LoggingLookups { get; set; }

        protected HtmlLink SkinStylesheet
        {
            get
            {
                var masterPage = GetCurrentPageView().Master;
                if (masterPage != null)
                    return (HtmlLink)masterPage.FindControl("skin");
                throw new PresentationException("Couldn't find skin stylesheet control.");
            }
            set
            {
                var masterPage = GetCurrentPageView().Master;
                if (masterPage == null) return;
                var skinStylesheet = (HtmlLink)masterPage.FindControl("skin");
                skinStylesheet.Href = value.Href;
            }
        }

        protected string BaseStylesheet
        {
            get
            {
                return GetStylesheetHrefValue("base");
            }
            set
            {
                var masterPage = GetCurrentPageView().Master;
                if (masterPage == null) return;
                var baseStylesheet = (HtmlLink)masterPage.FindControl("base");
                baseStylesheet.Href = value;
            }
        }

        protected string Ie6Stylesheet
        {
            get
            {
                return GetStylesheetHrefValue("ie6skin");
            }
            set
            {
                var masterPage = GetCurrentPageView().Master;
                if (masterPage == null) return;
                var baseStylesheet = (HtmlLink)masterPage.FindControl("ie6skin");
                baseStylesheet.Href = value;
            }
        }

        private string GetStylesheetHrefValue(string controlId)
        {
            var masterPage = GetCurrentPageView().Master;
            if (masterPage != null)
                return ((HtmlLink)masterPage.FindControl(controlId)).Href;
            throw new PresentationException("Couldn't find control named: " + controlId);
        }

        protected abstract Page GetCurrentPageView();

        private static void LogPageRequest()
        {
            QuoteHeader quoteHeader = null;

            if (SessionManager.IsMpq())
            {
                var mpqQuoteData = SessionManager.GetMPQQuoteData();

                if (mpqQuoteData != null)
                    quoteHeader = mpqQuoteData.quoteHeader;

                if (quoteHeader == null)
                    return;
            }
            else if (SessionManager.QuoteHeaderInSession())
            {
                quoteHeader = SessionManager.LoadHeaderFromSession();
            }

            if (quoteHeader == null) return;
            if (quoteHeader.SessionId == 0) return;

            var page = HttpContext.Current.Request.Path.Substring(HttpContext.Current.Request.Path.LastIndexOf('/') + 1);
            page = page.Substring(0, page.Contains("-") ? page.IndexOf('-') : page.IndexOf('.'));

            var logPub = new LogPublishServiceSoapClient();
            try
            {
                logPub.Transition(
                    Environment.MachineName,
                    quoteHeader.SessionId.ToString(),
                    quoteHeader.FormCode,
                    quoteHeader.PartnerId,
                    0,
                    page,
                    page,
                    "Page Requested"
                    );
            }
            // ReSharper disable EmptyGeneralCatchClause
            catch (Exception ex) // trying to debug an issue. Should stop general catch once I know which exception is getting thrown...
            // ReSharper restore EmptyGeneralCatchClause
            {
                ExperienceLogger.GetInstance.Log(
                    quoteHeader.SessionId,
                    quoteHeader.PartnerId,
                    quoteHeader.FormCode,
                    page,
                    ex.Message,
                    ex.StackTrace);
            }
            finally
            {
                logPub.Close();
            }
        }

        protected internal string GetEnumDescription(Enum value)
        {
            var fi = value.GetType().GetField(value.ToString());
            var attributes = (DescriptionAttribute[])fi.GetCustomAttributes(typeof(DescriptionAttribute), false);
            return (attributes.Length > 0) ? attributes[0].Description : value.ToString();
        }

        public virtual void OnViewPreInitialize()
        {
        }

        public virtual void OnViewInitialized()
        {
        }

        public virtual void OnViewInitializeComplete()
        {
        }

        public void OnViewPreLoad()
        {
            LogPageRequest();
        }

        public virtual void OnViewLoad()
        {
        }

        public virtual void OnViewLoadComplete()
        {
        }

        public virtual void OnViewPreRender()
        {
        }

        public virtual void OnViewRender(string pageText, HtmlTextWriter writer)
        {
        }

        public virtual void OnViewSaveStateComplete()
        {
        }

        public virtual void OnViewUnLoad()
        {
        }


        protected virtual string RedirectQueryString(params string[] qsParameterNameArgs)
        {
            return string.Empty;
        }

        protected virtual void DataBind()
        {
        }

        /// <summary>
        ///   Appends query string name value pairs to the end on a URL.
        ///   Will try to find and append values from post
        /// </summary>
        /// <param name = "existingUrl"></param>
        /// <param name = "qsParameterNameArgs">Name of Parameter</param>
        /// <returns>Appended Url</returns>
        protected internal string AppendQueryStringPrameterArray(string existingUrl, params string[] qsParameterNameArgs)
        {
            string returnValue;

            if (qsParameterNameArgs == null)
            {
                returnValue = existingUrl;
            }
            else
            {
                var queryString = new StringBuilder();
                queryString.Append(existingUrl);

                foreach (var qsParameterName in qsParameterNameArgs)
                {
                    try
                    {
                        queryString.AppendFormat("&{0}={1}", qsParameterName, _queryStringArray[qsParameterName]);
                    }
                    catch
                    {
                        // Do nothing
                    }
                }
                returnValue = queryString.ToString();
            }

            return returnValue;
        }

        /// <summary>
        ///   Gets current query string value
        /// </summary>
        /// <param name = "qsParamaeterName"></param>
        /// <param name = "qsParamaterDefaultValue">Default value</param>
        /// <returns></returns>
        protected internal string GetQueryStringValue(string qsParamaeterName, string qsParamaterDefaultValue)
        {
            if (string.IsNullOrEmpty(qsParamaeterName))
                throw new ArgumentNullException("qsParamaeterName");

            string returnValue;

            if (QueryStringNameValueArray.Contains(qsParamaeterName))
            {
                returnValue = QueryStringNameValueArray[qsParamaeterName].ToString();
            }
            else
            {
                returnValue = string.IsNullOrEmpty(GetCurrentPageView().Request.QueryString[qsParamaeterName]) ? qsParamaterDefaultValue : GetCurrentPageView().Request.QueryString[qsParamaeterName];
            }

            return returnValue;
        }

        /// <summary>
        ///   Gets current query string value, Default: Empty (Blank) String
        /// </summary>
        /// <param name = "QSParamaeterName"></param>
        /// <returns></returns>
        protected internal string GetQueryStringValue(string QSParamaeterName)
        {
            return GetQueryStringValue(QSParamaeterName, string.Empty);
        }

        /// <summary>
        ///   Updates query string value
        /// </summary>
        /// <param name = "name"></param>
        /// <param name = "value"></param>
        protected internal void SetQueryStringValue(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException("name");
            }

            _queryStringArray[name] = value;
        }

        /// <summary>
        ///   Resets query string values (default to post)
        /// </summary>
        protected internal void ResetQueryStringValues()
        {
            _queryStringArray = new Hashtable();
        }

        /// <summary>
        ///   Reloads page with current querystring values.
        /// </summary>
        /// <param name = "qsParameterNameArgs"></param>
        protected internal void ReLoadPageWithQuerystringSettings(params string[] qsParameterNameArgs)
        {
            GetCurrentPageView().Response.Redirect(RedirectQueryString(qsParameterNameArgs));
        }

        /// <summary>
        ///   Reloads Page
        /// </summary>
        protected internal void ReloadPage()
        {
            GetCurrentPageView().Response.Redirect(CurrentPage);
        }

        protected internal string EncodeQueryStringValue(string queryValue)
        {
            var returnValue = string.Empty;
            if (null != queryValue)
            {
                returnValue = GetCurrentPageView().Server.UrlEncode(queryValue);
            }

            return returnValue.Trim();
        }

        protected internal string DecodeQueryStringValue(string queryValue)
        {
            var returnValue = string.Empty;
            if (null != queryValue)
            {
                returnValue = GetCurrentPageView().Server.UrlDecode(queryValue);
            }

            return returnValue.Trim();
        }

        /// <summary>
        ///   Converts Enum Types to a (Bindable) DataTable
        ///   use : EnumToDataSource(typeof(Homesite.UI.Presentation.UIManager.CurrentContext))
        /// </summary>
        /// <param name = "enumType"></param>
        /// <returns></returns>
        protected internal DataTable EnumToDataSource(Type enumType)
        {
            //EnumToDataSource(typeof(Homesite.UI.Presentation.UIManager.CurrentContext));
            if (enumType == null)
            {
                throw new ArgumentNullException("enumType");
            }
            FieldInfo[] fieldInformations = enumType.GetFields();
            var dt = new DataTable();
            dt.Columns.Add(new DataColumn("key"));
            dt.Columns.Add(new DataColumn("value"));
            foreach (var fieldInformation in fieldInformations)
            {
                if (fieldInformation.IsLiteral)
                {
                    DataRow dr = dt.NewRow();
                    dr["value"] = fieldInformation.Name.Replace("_", " ");
                    dr["key"] = (int)fieldInformation.GetValue(null);
                    dt.Rows.Add(dr);
                }
            }
            return dt;
        }

        internal string GetPresentationKnoledgeBaseFilePath(QuoteHeader qHeader)
        {
            string path;
            if (this as BeginQuotePresenter != null)
            {
                path = HttpContext.Current.Server.MapPath(
                        String.Format("{0}controlsKB_BeginQuote.aspx.xml",
                                      WebConfigurationManager.AppSettings["knowledgeBasePath"]));
            }
            else if (this as PurchasePresenter != null && (qHeader.H03Ui_V3 || qHeader.H03Ui_V2))
            {
                path = HttpContext.Current.Server.MapPath(
                        String.Format("{0}controlsKB_Purchase.aspx.xml",
                                      WebConfigurationManager.AppSettings["knowledgeBasePath"]));
            }
            else
            {
                if (!qHeader.H06Ui_V2)
                {
                    path = HttpContext.Current.Server.MapPath(
                        String.Format("{0}controlsKB_iQuote.aspx.xml",
                                      WebConfigurationManager.AppSettings["knowledgeBasePath"]));
                }
                else
                {
                    path = HttpContext.Current.Server.MapPath(
                        String.Format("{0}controlsKB_iQuote.aspx_H06.xml",
                                      WebConfigurationManager.AppSettings["knowledgeBasePath"]));
                }
                if (SessionManager.IsHO3Test())
                {
                    path = path.Replace(".aspx", "_HO3Test.aspx");
                }
            }
            return path;
        }

        private string GetCoveragesKnoledgeBaseFilePath(QuoteHeader qHeader)
        {
            string path;
            if (!qHeader.H06Ui_V2)
            {
                path = HttpContext.Current.Server.MapPath(
                    String.Format("{0}coveragesKB_iQuote.aspx.xml",
                                  WebConfigurationManager.AppSettings["knowledgeBasePath"]));
            }
            else
            {
                path = HttpContext.Current.Server.MapPath(
                    String.Format("{0}coveragesKB_iQuote.aspx_H06.xml",
                                  WebConfigurationManager.AppSettings["knowledgeBasePath"]));
            }
            if (SessionManager.IsHO3Test())
            {
                path = path.Replace(".aspx", "_HO3Test.aspx");
            }
            return path;
        }

        protected internal void ApplyPreConditionPresentationRules()
        {
            var qHeader = (QuoteHeader)(View as TemplateControl).Page.Session["Header"];

            var controlsKB = GetPresentationKnoledgeBaseFilePath(qHeader);
            var coveragesKB = GetCoveragesKnoledgeBaseFilePath(qHeader);

            if (!File.Exists(controlsKB) && !File.Exists(coveragesKB))
            {
                throw new ExecutionEngineException("Control/Coverages KB not found!");
            }

            var controlRuleEngine = new PreConditionPresentationRuleEngine<PresentableDataSourceProvider>(
                controlsKB,
                qHeader,
                new PresentableDataSourceProvider());

            var enabledProjects = DirectWebConfigurationSettingsCache.StaticCollection.EnabledProjects();
            controlRuleEngine.UserInputData.Add("Project", enabledProjects);

            if (!(View is TemplateControl)) return;

            Control pageControl = (View as TemplateControl).Page;
            IterateControlPresentationRules(ref pageControl, controlRuleEngine);
        }

        public void IterateControlPresentationRules(ref Control control,
                                                    PreConditionPresentationRuleEngine<PresentableDataSourceProvider>
                                                        ruleEngine)
        {
            foreach (Control ctrl in control.Controls)
            {
                if (ctrl is IPresentable)
                {
                    //(this.View as TemplateControl).Page.Response.Write(string.Format("Found Control : {0}<br>", ctrl.ID));
                    ruleEngine.EvaluateControl((IPresentable)ctrl);
                }
                if (!ctrl.HasControls()) continue;
                if (!(ctrl is IQuestionSet) || (ctrl is IQuestionSet && ctrl.Visible))
                {
                    Control containerCtrl = ctrl;
                    IterateControlPresentationRules(ref containerCtrl, ruleEngine);
                }
            }
        }

        public void ProcessRules(ref Control control,
                                 PreConditionPresentationRuleEngine<PresentableDataSourceProvider> ruleEngine)
        {
            foreach (Control ctrl in control.Controls)
            {
                if (ctrl is IPresentable)
                {
                    //(this.View as TemplateControl).Page.Response.Write(string.Format("Found Control : {0}<br>", ctrl.ID));
                    ruleEngine.EvaluateControl((IPresentable)ctrl);
                }
                if (!ctrl.HasControls()) continue;

                if (!(ctrl is IQuestionSet) || (ctrl is IQuestionSet && ctrl.Visible))
                {
                    var containerCtrl = ctrl;
                    IterateControlPresentationRules(ref containerCtrl, ruleEngine);
                }
            }


            //TextWriter textWriter = new StringWriter();
            //HtmlTextWriter writer = new HtmlTextWriter(textWriter);
            //control.RenderControl(writer);
            //return textWriter.ToString();
        }

        protected ControlDataSource CloneControlDataSource(ControlDataSource cds)
        {
            var retCDS = new ControlDataSource
            {
                ControlName = cds.ControlName,
                DefaultValue = cds.DefaultValue,
                Items = new Dictionary<string, string>()
            };

            foreach (var key in cds.Items.Keys)
            {
                retCDS.Items.Add(key, cds.Items[key]);
            }

            return retCDS;
        }

        internal void DoNotCacheThisPage()
        {
            // stop browsers from using cache on back button hits.
            HttpContext.Current.Response.Cache.SetCacheability(HttpCacheability.NoCache);
            HttpContext.Current.Response.Cache.SetExpires(DateTime.Now.AddSeconds(-1));
            HttpContext.Current.Response.Cache.SetNoStore();
            HttpContext.Current.Response.AppendHeader("Pragma", "no-cache");
        }

        internal void CheckPurchased()
        {
            string redirTo;
            if (!SessionManager.IsMpq())
            {
                redirTo = "Welcome.aspx";
            }
            else
            {
                var mpqQuoteData = SessionManager.GetMPQQuoteData();
                redirTo = mpqQuoteData.RedirectTo.ToString();
            }

            if (QuoteService.PolicyPurchased())
                HttpContext.Current.Response.Redirect(redirTo);
        }


        internal void CheckFail()
        {
            try
            {
                var epicFail = (int)HttpContext.Current.Session["EpicFail"];
                if (epicFail == 1)
                {
                    if (SessionManager.IsMpq())
                    {
                        HttpContext.Current.Response.Redirect("http://www.progressive.com/");
                    }
                    else
                    {
                        HttpContext.Current.Response.Redirect("Welcome.aspx");
                    }
                }
            }
            catch (NullReferenceException)
            {
                HttpContext.Current.Session["EpicFail"] = 0;
            }
        }


        internal void CheckForm2Redirect(int formCode)
        {
            if (formCode == 2)
                HttpContext.Current.Response.Redirect("form2.aspx");
        }

        internal void CheckIneligible(int formCode, int partnerId)
        {
            if (formCode == 3 && partnerId == 1 && QuoteService.IsIneligibleState())
                HttpContext.Current.Response.Redirect("InEligible.aspx");
        }

        internal bool IsIneligible()
        {
            return QuoteService.IsIneligibleState();
        }

        internal void SetControlDataSource(ControlDataSource cds)
        {
            var iQuoteViewType = typeof(TView);
            var iQuoteViewProperties = iQuoteViewType.GetProperties();

            PropertyInfo controlProp;

            foreach (var viewProp in
                iQuoteViewProperties.Where(viewProp => viewProp.Name.ToUpper() == cds.ControlName.ToUpper()))
            {
                controlProp = iQuoteViewType.GetProperty(viewProp.Name);
                var obj = (IPresentable)controlProp.GetValue(View, null);
                if (obj != null)
                {
                    obj.Value = cds.DefaultValue;
                }
            }
        }


        internal void SetViewProperties(QuoteData qData)
        {
            var iQuoteViewType = typeof(TView);
            var iQuoteViewProperties = iQuoteViewType.GetProperties();
            var quoteDataType = typeof(QuoteData);

            object tmpVal;
            PropertyInfo quoteProp;

            foreach (var viewProp in iQuoteViewProperties)
            {
                quoteProp = quoteDataType.GetProperty(viewProp.Name);

                if (quoteProp == null || viewProp.GetValue(View, null) == null) continue;

                tmpVal = quoteProp.GetValue(qData, null);
                if (tmpVal == null) continue;

                switch (tmpVal.GetType().ToString())
                {
                    case "System.DateTime":
                        if ((DateTime)tmpVal > DateTime.MinValue)
                            ((IPresentable)(viewProp.GetValue(View, null))).Value =
                                ((DateTime)tmpVal).ToString("MM/dd/yyyy");
                        break;
                    case "System.Boolean":
                        ((IPresentable)(viewProp.GetValue(View, null))).Value = tmpVal.ToString();
                        break;
                    case "System.Nullable`1[System.Boolean]":
                        if (((bool?)tmpVal).HasValue)
                            ((IPresentable)(viewProp.GetValue(View, null))).Value =
                                ((bool?)tmpVal).Value.ToString();
                        break;
                    case "Homesite.ECommerce.Context.YesNo":
                        ((IPresentable)(viewProp.GetValue(View, null))).Value = ((YesNo)tmpVal).ToString();
                        break;
                    case "System.Nullable`1[Homesite.ECommerce.Context.YesNo]":
                        if (((YesNo?)tmpVal).HasValue)
                            ((IPresentable)(viewProp.GetValue(View, null))).Value =
                                (((YesNo?)tmpVal).Value).ToString();
                        break;
                    case "System.Int16":
                        ((IPresentable)(viewProp.GetValue(View, null))).Value = tmpVal.ToString();
                        break;
                    default:
                        if (!string.IsNullOrEmpty((string)tmpVal))
                            ((IPresentable)(viewProp.GetValue(View, null))).Value = (string)tmpVal;
                        break;
                }
            }
        }


        internal void SetMPQViewProperties(QuoteData qData, Control questionSet)
        {
            var quoteDataType = typeof(QuoteData);
            var quoteDataProps = quoteDataType.GetProperties();

            foreach (var qdProp in quoteDataProps)
            {
                string qdPropName = qdProp.Name;
                object quoteDataValue = qdProp.GetValue(qData, null);

                if (quoteDataValue != null)
                {
                    var qs = questionSet;
                    // If there's only one control in the questionSet passed
                    // in, try finding the named control inside that child.
                    if (qs.Controls.Count == 1) qs = qs.Controls[0];
                    var c = qs.FindControl(qdPropName);
                    // In case there are pages that only have one control, fall back to this.
                    if (c == null && qs.Controls.Count == 1) c = questionSet.FindControl(qdPropName);
                    if (c != null)
                    {
                        switch (quoteDataValue.GetType().ToString())
                        {
                            case "System.DateTime":
                                if ((DateTime)quoteDataValue > DateTime.MinValue)
                                    ((IPresentable)c).Value = ((DateTime)quoteDataValue).ToString("MM/dd/yyyy");
                                break;
                            case "System.Boolean":
                                ((IPresentable)c).Value = quoteDataValue.ToString();
                                break;
                            case "System.Nullable`1[System.Boolean]":
                                if (((bool?)quoteDataValue).HasValue)
                                    ((IPresentable)c).Value = ((bool?)quoteDataValue).Value.ToString();
                                break;
                            case "Homesite.ECommerce.Context.YesNo":
                                ((IPresentable)c).Value = ((YesNo)quoteDataValue).ToString();
                                break;
                            case "System.Nullable`1[Homesite.ECommerce.Context.YesNo]":
                                if (((YesNo?)quoteDataValue).HasValue)
                                    ((IPresentable)c).Value = (((YesNo?)quoteDataValue).Value).ToString();
                                break;
                            default:
                                var valueObject = (string)quoteDataValue;
                                if (!string.IsNullOrEmpty(valueObject))
                                {
                                    ((IPresentable)c).Value = valueObject.Replace("&", "&amp;");
                                }
                                break;
                        }
                    }
                }
            }
        }

        internal void CheckRedirect(QuoteHeader qHeader)
        {
            var redirect = new StringBuilder();

            bool redirecting =
                DirectWebConfigurationSettingsCache.StaticCollection.IsStateRedirect(qHeader.State, qHeader.FormCode) ||
                DirectWebConfigurationSettingsCache.StaticCollection.IsZipRedirect(qHeader.Zip, qHeader.FormCode);

            if (redirecting)
            {
                if (qHeader.IsRetrieve)
                {
                    string token =
                        DirectWebConfigurationSettingsCache.StaticCollection.GetIQuoteRetrieveRedirect(
                            qHeader.PartnerId,
                            qHeader.State,
                            qHeader.QuoteNumber);

                    redirect.Append("RetrieveTransfer.aspx?");
                    redirect.Append(token);
                }
                else
                {
                    redirect.Append(DirectWebConfigurationSettingsCache.StaticCollection.Info("RedirectUrl"));
                    redirect.Append("Partner.aspx?");

                    redirect.AppendFormat("zip={0}&", qHeader.Zip);
                    redirect.AppendFormat("Mktid={0}&", qHeader.MarketingId);

                    // "existing for pgr", whatever that means, we have awesome BRDs.
                    if (qHeader.PartnerId == 8)
                    {
                        // yeah.. iquote partner aspx doesn't actually use these fields.
                        redirect.Append("cust_id=0&");
                        redirect.Append("track_id=&");
                        redirect.Append("prog_status=&");
                    }

                    // form code
                    switch (qHeader.FormCode)
                    {
                        case 4:
                            redirect.Append("ho_prod=r&");
                            break;
                        case 6:
                            redirect.Append("ho_prod=c&");
                            break;
                        default:
                            redirect.Append("ho_prod=h&");
                            break;
                    }

                    // effective date
                    if (!(string.IsNullOrEmpty(qHeader.PolicyEffectiveDate)))
                        redirect.AppendFormat("effdt={0}&", qHeader.PolicyEffectiveDate);

                    if (HttpContext.Current.Session["QuoteData"] != null)
                    {
                        var qData = (QuoteData)HttpContext.Current.Session["QuoteData"];

                        if (!(string.IsNullOrEmpty(qData.firstName)))
                            redirect.AppendFormat("Fname={0}&", qData.firstName);

                        if (!(string.IsNullOrEmpty(qData.lastName)))
                            redirect.AppendFormat("Lname={0}&", qData.lastName);

                        if (!(string.IsNullOrEmpty(qData.propertyAddressLine1)))
                            redirect.AppendFormat("Addr1={0}&", qData.propertyAddressLine1);

                        if (!(string.IsNullOrEmpty(qData.propertyAddressLine2)))
                            redirect.AppendFormat("Addr2={0}&", qData.propertyAddressLine2);

                        if (!(string.IsNullOrEmpty(qData.ssn)))
                            redirect.AppendFormat("SSN={0}&", qData.ssn);

                        if (qData.dateOfBirth != new DateTime())
                            redirect.AppendFormat("DOB={0}&", qData.dateOfBirth.ToString("MMddyyyy"));
                    }
                }
            }

            if (redirecting)
                HttpContext.Current.Response.Redirect(redirect.ToString());
        }


        /// <summary>
        ///   Renders the iQuote.data object in the client script.
        /// </summary>
        protected void PersistQuoteHeaderToClientScript(QuoteHeader quoteHeader)
        {
            var qHeader = new QuoteHeader();
            Type type = qHeader.GetType();
            PropertyInfo[] prop = type.GetProperties();

            foreach (var property in prop)
            {
                AddClientSideDataItem(property.Name, property.GetValue(quoteHeader, null));
            }
        }

        public void ShowErrorMessage(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                GetCurrentPageView().ClientScript.RegisterClientScriptBlock(
                    View.GetType(),
                    "ShowErrorMessage",
                    string.Format(
                        @"$(document).ready(function(){{ var e = new iQuote.Error(); e.ShowAttentionArea(""{0}""); }});",
                        message),
                    true);
            }
        }

        /// <summary>
        ///   If the specified test gets a group other than "A" ("B", "C", etc.), add a class to the html element of the page
        ///   indicating that the user is in the associated "test" flow.  This is just a simplified
        ///   pattern for A/B tests that mainly involve CSS/JS tweaks.
        /// 
        ///   Updated to support more than two test groups as part of the H03Yield project (Story 337). 
        ///   Blame Rich Pires if it breaks.
        /// 
        /// </summary>
        public void RegisterABTest(string testName)
        {
            var client = new UIService();
            String testGroup = client.GetABTestGroup(testName);
            // Only register the test if it is turned on.
            if (Regex.IsMatch(testGroup, "^[B-Z]{1}$"))
            {
                /* Add the AB test and associated group to the javascript AB test object.
                 * Example: iQuote.Util.ABTest['H03Yield'] = 'B' */
                GetCurrentPageView().ClientScript.RegisterClientScriptBlock(
                    View.GetType(),
                    string.Format("AddABTest{0}", testName),
                    string.Format(@"iQuote.Util.ABTests['{0}'] = '{1}';", testName, testGroup),
                    true);
            }
        }


        // wrap client script that creates objectes with this:


        public void AddJavascriptBlock(string scriptName, string script)
        {
            GetCurrentPageView().ClientScript.RegisterClientScriptBlock(GetType(), scriptName, script);
        }

        public void AddJavascriptInclude(string scriptName, Uri scriptSource)
        {
            GetCurrentPageView().ClientScript.RegisterClientScriptInclude(scriptName, scriptSource.ToString());
        }

        /// <summary>
        ///   Adds a script to get loaded *after* DOM load. Use for all scripts used that aren't
        ///   hosted locally.
        /// </summary>
        /// <param name = "scriptName"></param>
        /// <param name = "scriptSource"></param>
        public void AddRemoteScript(string scriptName, Uri scriptSource)
        {
            var remoteScriptLoader = new StringBuilder();
            remoteScriptLoader.Append("<script language='javascript' type='text/javascript'>//<![CDATA[");
            remoteScriptLoader.Append(Environment.NewLine);
            remoteScriptLoader.Append("$(document).ready(function() { util.LoadRemoteScript('");
            remoteScriptLoader.Append(scriptSource.ToString());
            remoteScriptLoader.Append("');});");
            remoteScriptLoader.Append(Environment.NewLine);
            remoteScriptLoader.Append("//]]></script>");

            GetCurrentPageView().ClientScript.RegisterClientScriptBlock(GetType(), scriptName,
                                                                        remoteScriptLoader.ToString());
        }

        internal QuoteSummary GetQuoteSummaryFromList(QuoteHeader qHdr)
        {
            var qSvc = new QuoteService();
            QuoteSummary qSum = null;
            var qSumList = qSvc.GetQuoteSummaryListForSession(qHdr);
            switch (qHdr.ApexBillingOption)
            {
                case "100":
                    qSum = (from item in qSumList
                            where item.NumInstallments == 0
                            select item).First();
                    break;
                case "40":
                    qSum = (from item in qSumList
                            where item.NumInstallments == 3
                            select item).First();
                    break;
                case "25":
                    qSum = (from item in qSumList
                            where item.NumInstallments == 9
                            select item).First();
                    break;
            }            
            return qSum;
        }

        internal void PersistQuoteSummary(QuoteHeader qHdr)
        {
            var qSvc = new QuoteService();
            QuoteSummary qSum = null;
            if (qHdr.IsApexState)
            {
                qSum = this.GetQuoteSummaryFromList(qHdr);
            }
            else
            { 
                qSum = qSvc.GetQuoteSummaryForSession(qHdr);
            }
            var qsdict = new Dictionary<string, object>
                             {
                                 {"Base", qSum.Base ?? "0"},
                                 {"Discounts", qSum.Discounts ?? "0"},
                                 {"Downpayment", qSum.Downpayment ?? "0"},
                                 {"LiabilityLimit", qSum.LiabilityLimit ?? "0"},
                                 {"MonthlyPayPlan", qSum.MonthlyPayPlan ?? "0"},
                                 {"EftStateFee", qSum.EftStateFee ?? "0"},
                                 {"PropertyLimit", qSum.PropertyLimit ?? "0"},
                                 {"QuoteNumber", qSum.QuoteNumber ?? "0"},
                                 {"TotalPremium", qSum.TotalPremium ?? "0"},
                                 {"NumInstallments", qSum.NumInstallments},
                                 {"PolicyEffectiveDate", qSum.PolicyEffectiveDate ?? "0"}                                 
                             };
            PersistDictionaryToClientScript("QuoteSummary", qsdict);
        }

        internal void PersistQuoteSummaryList(QuoteHeader qHdr)
        {
            var qSvc = new QuoteService();
            var qSum = qSvc.GetQuoteSummaryListForSession(qHdr);
            Dictionary<string, object> qsdict = new Dictionary<string, object>();
            //Dictionary<string, object> qsdict;
            var output = new StringBuilder();
            output.Append(Environment.NewLine);
            output.Append(JsStart);
            output.Append(Environment.NewLine);

            if (!_persistDataObjectRendered)
            {
                output.Append("iQuote.persistData = {};");
                output.Append(Environment.NewLine);
                _persistDataObjectRendered = true;
            }

            output.Append("iQuote.persistData." + "QuoteSummaryList" + " = (function() { function constructor() { } ");
            output.Append(" constructor = [{");

            for (int i = 0; i < qSum.Length; i++)
            {
                qsdict = new Dictionary<string, object>
                             {
                                 {"Base", qSum[i].Base ?? "0"},
                                 {"Discounts", qSum[i].Discounts ?? "0"},
                                 {"Downpayment", qSum[i].Downpayment ?? "0"},
                                 {"LiabilityLimit", qSum[i].LiabilityLimit ?? "0"},
                                 {"MonthlyPayPlan", qSum[i].MonthlyPayPlan ?? "0"},
                                 {"EftStateFee", qSum[i].EftStateFee ?? "0"},
                                 {"PropertyLimit", qSum[i].PropertyLimit ?? "0"},
                                 {"QuoteNumber", qSum[i].QuoteNumber ?? "0"},
                                 {"TotalPremium", qSum[i].TotalPremium ?? "0"},
                                 {"NumInstallments", qSum[i].NumInstallments},
                                 {"PolicyEffectiveDate", qSum[i].PolicyEffectiveDate ?? "0"}
                             };




                HttpContext.Current.Session["QuoteSummaryList"] = qsdict;




                var data = new StringBuilder();
                foreach (var key in qsdict.Keys.Where(key => key != "ExtensionData" && qsdict[key] != null))
                {
                    data.Append("\"");
                    data.Append(key);
                    data.Append("\": \"");
                    data.Append(qsdict[key].ToString().Replace(@"""", @"\"""));
                    data.Append("\", ");
                }

                // add the data to the js with the last two characters (", ") stripped.
                output.Append(data.Remove((data.Length - 2), 2).ToString());
                output.Append("},{");
               
            }
             
            output.Length--;
            output.Length--;
            output.Append(" ];");
            output.Append("return constructor;");
            output.Append("})();");

            output.Append(Environment.NewLine);
            output.Append(JsEnd);
            output.Append(Environment.NewLine);

            GetCurrentPageView().ClientScript.RegisterClientScriptBlock(GetType(), "QuoteSummaryList", output.ToString());

            QuoteSummary quoteSum = this.GetQuoteSummaryFromList(qHdr);

            var quotedict = new Dictionary<string, object>
                             {
                                 {"Base", quoteSum.Base ?? "0"},
                                 {"Discounts", quoteSum.Discounts ?? "0"},
                                 {"Downpayment", quoteSum.Downpayment ?? "0"},
                                 {"LiabilityLimit", quoteSum.LiabilityLimit ?? "0"},
                                 {"MonthlyPayPlan", quoteSum.MonthlyPayPlan ?? "0"},
                                 {"EftStateFee", quoteSum.EftStateFee ?? "0"},
                                 {"PropertyLimit", quoteSum.PropertyLimit ?? "0"},
                                 {"QuoteNumber", quoteSum.QuoteNumber ?? "0"},
                                 {"TotalPremium", quoteSum.TotalPremium ?? "0"},
                                 {"NumInstallments", quoteSum.NumInstallments},
                                 {"PolicyEffectiveDate", quoteSum.PolicyEffectiveDate ?? "0"}                                 
                             };
            PersistDictionaryToClientScript("QuoteSummary", quotedict);
        }

        public void PersistBoomerang()
        {
            string boomrPath =
                WebConfigurationManager.AppSettings["BoomerangPath"];


            var boomerang =
                new Dictionary<string, object>();
            boomerang.Add("UserIp", HttpContext.Current.Request.UserHostAddress);
            boomerang.Add("Beacon", string.Format("{0}Boomerang.asmx/Catch", boomrPath));
            boomerang.Add("BandwidthImages", string.Format("{0}boomerang/", boomrPath));
            PersistDictionaryToClientScript("Boomerang", boomerang);
        }

        public void Render(HtmlTextWriter writer)
        {
            // render the iQuote.data object:
            GetCurrentPageView().ClientScript.RegisterClientScriptBlock(GetType(), "QuoteHeader",
                                                                        PersistQuoteHeaderToClientScript());

            // render logging lookups:
            GetCurrentPageView().ClientScript.RegisterClientScriptBlock(GetType(), "LoggingLookups",
                                                                        PersistLoggingLookupsToClientScript());

            // render the TrackingURL
            var trackingURL = PersistTrackingURLToClientScript();
            if (!string.IsNullOrEmpty(trackingURL))
            {
                GetCurrentPageView().ClientScript.RegisterClientScriptBlock(GetType(), "TrackingURL", trackingURL);
            }

            // persist GA trackers from config file:
            PersistGoogleAnalyticsData();

            string pageText;

            using (var sw = GetPageWriter())
            {
                pageText = sw.ToString();
            }

            // process iQuote Tags
            var taggedPage = iQuoteTags.ProcessTags(GetCurrentPageView().Request.RawUrl, pageText);

            //writer.Write(pageText);
            writer.Write(taggedPage);
        }

        public void PersistGoogleAnalyticsData()
        {
            if (WebConfigurationManager.AppSettings["gaTracker"] == null) return;

            var gaTrackerList = new Dictionary<string, object>
                                    {
                                        {"Progressive", WebConfigurationManager.AppSettings["gaTracker"]},
                                        {"Homesite", WebConfigurationManager.AppSettings["hsgaTracker"]}, 
                                        {"PGRStartTime", HttpContext.Current.Session["st"]},
                                        {"PGRFromPage", HttpContext.Current.Session["fromPage"]}
                                    };

            PersistDictionaryToClientScript("GoogleAnalytics", gaTrackerList);
        }

        // Yield - story # 612 - ITR # 9745 - Start
        /// <summary>
        ///   This will get BoldChat vender information so that users can chat with our agents
        /// </summary>
        public void PersistBoldChatData()
        {     
            var boldChatParams = new Dictionary<string, object>
                                    {
                                        {"AccountID", DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatAccountID()},
                                        {"RightRailBtnId", DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatBdid()},
                                        {"HelpTextBtnId", DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatHelpTextBdid()},
                                        {"MortgageBtnId", DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatMortgageBdid()},
                                        {"BoldChatUrl", DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatUrl()},
                                    };

            PersistDictionaryToClientScript("BoldChat", boldChatParams);
        }
        // Yield - story # 612 - ITR # 9745 - End

        /// <summary>
        ///   This compresses and obfuscates the output of the page. It strips all comments,
        ///   removes all spacing control characters, and any multiple successive spaces.
        /// </summary>
        private static string ShrinkAndObfuscate(string pageText)
        {
            var commentRegex = new Regex("((<!-- )((?!<!-- ).)*( -->))(\r\n)*",
                                                               RegexOptions.Singleline);
            var whiteSpaceRegex = new Regex("[\r\n\f\t]");
            var multipleSpaces = new Regex("[ ]{2,666}");
            var cdataRegex = new Regex("\\/\\/<!\\[CDATA\\[");
            var cdataEnd = new Regex("\\/\\/\\]\\]>", RegexOptions.Singleline);

            var formattedOutput = whiteSpaceRegex.Replace(
                multipleSpaces.Replace(
                    cdataEnd.Replace(
                        cdataRegex.Replace(
                            cdataRegex.Replace(
                                commentRegex.Replace(pageText,
                                                     string.Empty),
                                string.Empty),
                            string.Empty),
                        " "),
                    " "),
                string.Empty);

            return formattedOutput;
        }

        private StringWriter GetPageWriter()
        {
            var sw = new StringWriter();
            var htmlWriter = new HtmlTextWriter(sw);

            ((IView)View).RenderPage(htmlWriter);

            return sw;
        }

        public void AddClientSideDataItem(string key, object value)
        {
            _clientData.Add(key, value);
        }


        public void SetPageSteps(QuoteHeader qHeader)
        {
            var pageSteps = new List<string>();

            var isMpq = DirectWebConfigurationSettingsCache.StaticCollection.IsMPQ();
            if (!isMpq)
            {
                switch (qHeader.FormCode)
                {
                    case 2:
                    case 3:
                        if (SessionManager.IsHO3Test())
                        {
                            pageSteps.Add("About_You");
                            pageSteps.Add("Property_Info");
                            pageSteps.Add("p3");
                            pageSteps.Add("p4");
                            pageSteps.Add("p5");
                            pageSteps.Add("Coverage");
                            pageSteps.Add("Purchase");
                        }
                        else
                        {
                            pageSteps.Add("Your Address");
                            pageSteps.Add("About You");
                            pageSteps.Add("Property Info");
                            pageSteps.Add("Additional Info");
                            pageSteps.Add("Coverage");
                            pageSteps.Add("Purchase");
                        }
                        break;
                    case 4:
                        if (DirectWebConfigurationSettingsCache.StaticCollection.H04UiVersion2Enabled(qHeader.State))
                        {
                            pageSteps.Add("About You");
                            pageSteps.Add("Coverage");
                            pageSteps.Add("Purchase");
                        }
                        else
                        {
                            pageSteps.Add("About You");
                            pageSteps.Add("Property Info");
                            pageSteps.Add("iQuote");
                            pageSteps.Add("Additional Info");
                            pageSteps.Add("Coverage");
                            pageSteps.Add("Purchase");
                        }
                        break;
                    case 6:
                        if (DirectWebConfigurationSettingsCache.StaticCollection.H06UiVersion2Enabled(qHeader.State))
                        {
                            pageSteps.Add("Condo Information");
                            pageSteps.Add("Coverage");
                            pageSteps.Add("Purchase");
                        }
                        else
                        {
                            pageSteps.Add("About You");
                            pageSteps.Add("Property Info");
                            pageSteps.Add("iQuote");
                            pageSteps.Add("Additional Info");
                            pageSteps.Add("Coverage");
                            pageSteps.Add("Purchase");
                        }
                        break;
                    default:
                        throw new Exception("No form code or form code doesn't appear valid...");
                }
            }
            else
            {
                switch (qHeader.FormCode)
                {
                    case 2:
                    case 3:
                        pageSteps.Add("p1");
                        pageSteps.Add("p2");
                        pageSteps.Add("p25");
                        pageSteps.Add("p3");
                        pageSteps.Add("p4");
                        break;
                    case 4:
                        pageSteps.Add("p1");
                        break;
                    case 6:
                        pageSteps.Add("p1");
                        break;
                    default:
                        throw new Exception("No form code or form code doesn't appear valid...");
                }
            }


            PersistListToClientScript("PageSteps", pageSteps);
        }


        public void PersistDictionaryToClientScript(string persistName, Dictionary<string, object> objectToPersist)
        {
            HttpContext.Current.Session[persistName] = objectToPersist;

            var output = new StringBuilder();

            output.Append(Environment.NewLine);
            output.Append(JsStart);
            output.Append(Environment.NewLine);

            if (!_persistDataObjectRendered)
            {
                output.Append("iQuote.persistData = {};");
                output.Append(Environment.NewLine);
                _persistDataObjectRendered = true;
            }

            output.Append("iQuote.persistData." + persistName + " = (function() { function constructor() { } ");
            output.Append(" constructor = {");


            var data = new StringBuilder();
            foreach (var key in objectToPersist.Keys.Where(key => key != "ExtensionData" && objectToPersist[key] != null))
            {
                data.Append("\"");
                data.Append(key);
                data.Append("\": \"");
                data.Append(objectToPersist[key].ToString().Replace(@"""", @"\"""));
                data.Append("\", ");
            }

            // add the data to the js with the last two characters (", ") stripped.
            output.Append(data.Remove((data.Length - 2), 2).ToString());

            output.Append(" }; ");
            output.Append("return constructor;");
            output.Append("})();");

            output.Append(Environment.NewLine);
            output.Append(JsEnd);
            output.Append(Environment.NewLine);

            GetCurrentPageView().ClientScript.RegisterClientScriptBlock(GetType(), persistName, output.ToString());
        }

        public void PersistListToClientScript(string persistName, List<string> listToPersist)
        {
            HttpContext.Current.Session[persistName] = listToPersist;

            var output = new StringBuilder();

            output.Append(Environment.NewLine);
            output.Append(JsStart);
            output.Append(Environment.NewLine);

            if (!_persistDataObjectRendered)
            {
                output.Append("iQuote.persistData = {};");
                output.Append(Environment.NewLine);
                _persistDataObjectRendered = true;
            }

            output.Append("iQuote.persistData." + persistName + " = (function() { function constructor() { } ");
            //output.Append(" constructor." + persistName + " = {");

            output.Append(" constructor = [");


            var data = new StringBuilder();
            foreach (string key in listToPersist)
            {
                if (key != "ExtensionData")
                {
                    data.Append("\'");
                    data.Append(key);
                    data.Append("\', ");
                }
            }

            // add the data to the js with the last two characters (", ") stripped.
            output.Append(data.Remove((data.Length - 2), 2).ToString());

            output.Append(" ]; ");
            output.Append("return constructor;");
            output.Append("})();");

            output.Append(Environment.NewLine);
            output.Append(JsEnd);
            output.Append(Environment.NewLine);

            GetCurrentPageView().ClientScript.RegisterClientScriptBlock(GetType(), persistName, output.ToString());
        }

        /// <summary>
        ///   This function persists a QuoteHeader object to the Client Script,
        ///   generating an object named iQuote.data.quoteHeader.
        /// </summary>
        /// <returns></returns>
        private string PersistQuoteHeaderToClientScript()
        {
            var output = new StringBuilder();

            if (_clientData.Count == 0)
            {
                // Give us something to render.  Otherwise, the initialization JS breaks in a few places.
                AddClientSideDataItem("nothing", "");
            }

            var data = new StringBuilder();
            foreach (string key in _clientData.Keys.Where(key => key != "ExtensionData"))
            {
                data.Append("\'");
                data.Append(key);
                data.Append("\': \'");
                data.Append(_clientData[key]);
                data.Append("\', ");
            }

            output.Append(Environment.NewLine);
            output.Append(JsStart);
            output.Append(Environment.NewLine);
            output.Append("iQuote.data = (function() { function constructor() { } ");
            output.Append(" constructor.quoteHeader = {");

            // add the data to the js with the last two characters (", ") stripped.
            output.Append(data.Remove((data.Length - 2), 2).ToString());

            output.Append(" }; ");
            output.Append("return constructor;");
            output.Append("})();");

            output.Append(Environment.NewLine);
            output.Append(JsEnd);
            output.Append(Environment.NewLine);

            return output.ToString();
        }


        private string PersistLoggingLookupsToClientScript()
        {
            var output = new StringBuilder();
            string gaTracker = DirectWebConfigurationSettingsCache.StaticCollection.GoogleAnalyticsTracker();
            string boldChatAccountID = DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatAccountID();
            string boldChatBdid = DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatBdid();
            // Yield - story # 612 - ITR # 9745 - Start
            string boldChatHelpTextBdid = DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatHelpTextBdid();
            string boldChatMortgageBdid = DirectWebConfigurationSettingsCache.StaticCollection.GetBoldChatMortgageBdid();
            // Yield - story # 612 - ITR # 9745 - End

            // fetch lookup data
            if (LoggingLookups == null)
            {
                var logPublishClient = new LogPublishServiceSoapClient();

                try
                {
                    output.Append(Environment.NewLine);
                    output.Append(JsStart);
                    output.Append(Environment.NewLine);
                    output.Append("iQuote.Log = {};");
                    output.Append(Environment.NewLine);

                    output.Append("iQuote.Log.Tracker = {");
                    output.Append("\'");
                    output.Append("GATracker");
                    output.Append("\': \'");
                    output.Append(gaTracker);
                    output.Append("\'");
                    output.Append("};");
                    output.Append(Environment.NewLine); 
                    output.Append(logPublishClient.GetNavigationLookupJSON());
                    output.Append(Environment.NewLine);
                    output.Append(logPublishClient.GetProcessStepLookupJSON());
                    output.Append(Environment.NewLine);
                    output.Append(logPublishClient.GetPlatformLookupJSON());
                    output.Append(Environment.NewLine);
                    output.Append(JsEnd);
                    output.Append(Environment.NewLine);
                }
                finally
                {
                    logPublishClient.Close();
                }
            }

            return output.ToString();
        }

        /// <summary>
        /// Retrieves the tracking URL from the configuration cache and creates a script tag
        /// that assigns the value to iQuote.TrackingURL.
        /// </summary>
        /// <returns>A string representing</returns>
        private string PersistTrackingURLToClientScript()
        {
            var output = string.Empty;

            var trackingURL = DirectWebConfigurationSettingsCache.StaticCollection.GetTrackingURL();

            if (!string.IsNullOrEmpty(trackingURL))
            {
                var builder = new StringBuilder();

                builder.Append(Environment.NewLine);
                builder.Append(JsStart);
                builder.Append(Environment.NewLine);
                builder.Append("iQuote.TrackingURL = \'");
                builder.Append(trackingURL);
                builder.Append("\';");
                builder.Append(Environment.NewLine);
                builder.Append(JsEnd);
                builder.Append(Environment.NewLine);

                output = builder.ToString();
            }

            return output;
        }


        public void PartnerControlRedirect(IView view)
        {
            var page = (Page)view;
            string pageFileName = page.Request.AppRelativeCurrentExecutionFilePath.Substring(2);
            string pageControlName = pageFileName.Replace(".aspx", string.Empty);

            string partnerControl =
                DirectWebConfigurationSettingsCache.StaticCollection.GetPartnerControl(pageControlName);
            Redirect(pageFileName, partnerControl);
        }

        private static void Redirect(string pageFileName, string partnerControl)
        {
            if (partnerControl != pageFileName && !(string.IsNullOrEmpty(partnerControl)))
            {
                if (SessionManager.IsHO3Test())
                {
                    if (partnerControl.Contains("BeginQuote") || partnerControl.Contains("iQuote") ||
                        partnerControl.Contains("Purchase"))
                    {
                        // There's no custom Welcome page
                        partnerControl = partnerControl.Replace(".aspx", "2.aspx");
                    }
                }

                if (HttpContext.Current.Request.QueryString["rE"] != null)
                {
                    HttpContext.Current.Response.Redirect(partnerControl + "?rE=true");
                }
                else
                {
                    HttpContext.Current.Response.Redirect(partnerControl);
                }
            }
        }


        public void PartnerControlRedirect(IView view, int? formCode)
        {
            var page = (Page)view;
            string pageFileName = page.Request.AppRelativeCurrentExecutionFilePath.Substring(2);
            string pageControlName = pageFileName.Replace(".aspx", string.Empty);

            string partnerControl =
                DirectWebConfigurationSettingsCache.StaticCollection.GetPartnerControl(pageControlName, formCode);
            Redirect(pageFileName, partnerControl);
        }

        protected void SkinPage(IView view)
        {
            var thisPage = (Page)view;

            var isMpq = DirectWebConfigurationSettingsCache.StaticCollection.IsMPQ();
            if (!isMpq)
            {
                var partnerId = DirectWebConfigurationSettingsCache.StaticCollection.PartnerID(thisPage.Request.Url);
                //ITR#7580
                // ESurance redirect happens here, it's the first place we get PID and it's called from every page.
                //if (partnerId == 28 && !HttpContext.Current.Request.Url.ToString().Contains("Welcome-Esurance"))
                //{
                //    HttpContext.Current.Response.Redirect("Welcome-Esurance.aspx");
                //}


                thisPage.MasterPageFile = DirectWebConfigurationSettingsCache.StaticCollection.MasterPage();

                // partner specific stylesheet
                if (partnerId != 8)
                    SkinStylesheet.Href = DirectWebConfigurationSettingsCache.StaticCollection.StyleSheet();

                // set base stylesheet
                var baseStyleSheetFileName = (partnerId != 8)
                                                    ? GenericBaseStylesheetName
                                                    : GenericPGRBaseStylesheetName;

                if (((this is BeginQuotePresenter) || (this is iQuotePresenter) || (this is PurchasePresenter)) &&
                    SessionManager.IsHO3Test())
                {
                    baseStyleSheetFileName = "progressive2.css";
                    Ie6Stylesheet = "~/Stylesheets/ie6-progressive2.css";
                }

                var stylesheet = new StringBuilder();
                if (thisPage.Request.ApplicationPath != "/") // if you're using the dev server..
                    stylesheet.Append(thisPage.Request.ApplicationPath);

                stylesheet.Append(StylesheetSubfolder);
                stylesheet.Append(baseStyleSheetFileName);
                BaseStylesheet = stylesheet.ToString();
            }
            else
            {
                thisPage.MasterPageFile = DirectWebConfigurationSettingsCache.StaticCollection.MasterPage();
                SkinStylesheet.Href = DirectWebConfigurationSettingsCache.StaticCollection.StyleSheet();

                // set base stylesheet
                const string baseStyleSheetFileName = "MPQ-base.css";

                var stylesheet = new StringBuilder();
                if (thisPage.Request.ApplicationPath != "/") // if you're using the dev server..
                    stylesheet.Append(thisPage.Request.ApplicationPath);

                stylesheet.Append(StylesheetSubfolder);
                stylesheet.Append(baseStyleSheetFileName);
                BaseStylesheet = stylesheet.ToString();
            }
        }

        protected void LogPageLoad(QuoteHeader qHeader)
        {
            if (qHeader == null) return;
            var pageName = GetType().Name.Replace("Presenter", "");
            ExperienceLogger.GetInstance.Log(qHeader.SessionId, qHeader.PartnerId, qHeader.FormCode, pageName, "",
                                             "Loading Page");
            SessionManager.SetLoadStartTime();
        }

        protected void InitPartnerConfiguration(IView view)
        {
            var svc = new UIService();
            svc.InitalizeUI(((Page)view).Request.Url.ToString());
        }

        public void Plat2RedirectCheck(QuoteHeader qHeader, string sourceId, System.Collections.Specialized.NameValueCollection queryString)
        {
            bool isPlat20FlowEnabled = false;
            string redirectTo = "";

            try
            {
                isPlat20FlowEnabled = new UIService().GetIsPlat20FlowEnabled(
                      qHeader.PartnerId,
                      qHeader.State,
                      qHeader.FormCode,
                      BrowserCheck.isMobileBrowser(),
                      ref redirectTo);
            }
            catch (Exception e)
            {
                ExperienceLogger.GetInstance.Log(-1, sourceId, qHeader.PartnerId, qHeader.FormCode, "QuoteStart", "Yes",
                    String.Format("Plat Flow check threw exception. {0}: {1}", e.GetType(), e.Message), DateTime.Now.Millisecond);
            }

            if (isPlat20FlowEnabled)
            {
                // take the returned host and add our current query parameters to it.
                // additionally, add the gid (sourceId).
                UriBuilder uri = new UriBuilder(redirectTo);
                StringBuilder sb = new StringBuilder(uri.Query);
                if (sb.Length > 0)
                {
                    // remove '?', setting '?' to uri.Query always adds one back in.
                    sb.Remove(0, 1);
                }
                if (sb.Length > 0)
                {
                    sb.Append("&");
                }
                sb.AppendFormat("{0}={1}&", "gid", System.Web.HttpUtility.UrlEncode(sourceId));
                foreach (string key in queryString)
                {
                    string value = queryString[key];
                    sb.AppendFormat("{0}={1}&", System.Web.HttpUtility.UrlEncode(key), System.Web.HttpUtility.UrlEncode(value));
                }
                if (sb.Length > 0)
                {
                    sb.Length -= 1;
                }
                uri.Query = sb.ToString();

                ExperienceLogger.GetInstance.Log(-1, sourceId, qHeader.PartnerId, qHeader.FormCode, "QuoteStart", "Yes",
                    String.Format("Plat Flow condition is true.Redirecting to URL={0}", uri.Uri.AbsoluteUri), DateTime.Now.Millisecond);
                System.Web.HttpContext.Current.Response.Redirect(uri.Uri.AbsoluteUri);
            }
        }

        public string CreateRwdRedirectUrl(QuoteHeader qHeader)
        {
            var urlBase = new UIService().ResponsiveWebRedireectBase();

            /*
             * HACK:
             * This is a hack. Because we are using two different partner id's, but at the point of entry the partner id
             * is the same we need to redirect using a different virtual directory.
             */
            if (qHeader.PartnerId == 1 &&
                qHeader.FormCode == 3)
            {
                urlBase = urlBase.Replace("RwdDirectWeb", "RwdDirectWebHO3");
            }

            // ITR 9018
            if (qHeader.PartnerId == 8 &&
                qHeader.FormCode == 3)
            {
                urlBase = urlBase.Replace("RwdDirectWeb", "RwdPGRHO3");                
            }
            /*
             * HACK:
             * The hack is over.
             */

            /* ITR 9948, QC 516, 518 - If zip/formcode is null, it's added. 
            This scenario happens when an invalid zip present in RWD partner link causes 
            redirection to CW welcome page. When the zip is corrected through the CW welcome page, 
            the user is redirected back to RWD.   
            */
            if (string.IsNullOrEmpty(HttpContext.Current.Request.QueryString["zip"]) || string.IsNullOrEmpty(HttpContext.Current.Request.QueryString["formcode"]))
            {
                return string.Format("{0}?{1}", urlBase, "zip=" + qHeader.Zip + "&formcode=" + qHeader.FormCode);
            }
            else
            {
                return string.Format("{0}?{1}", urlBase, HttpContext.Current.Request.QueryString.ToString());
            }       
        }
    }
}

