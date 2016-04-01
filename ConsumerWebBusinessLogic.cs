using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.ServiceModel;
using System.Reflection;
using FNV;
using Homesite.ECommerce.Context;
using Homesite.ECommerce.Configuration;
using Homesite.ECommerce.IQuote;
using Homesite.ECommerce.ServiceLibrary.OLSService;
using Homesite.ECommerce.ServiceLibrary.ValidationService;
using Homesite.IQuote.BusinessLogic;
using Homesite.IQuote.SessionBroker;
using Homesite.ECommerce.DAC;
using Homesite.IQuote.ServiceInterfaces;
using Homesite.ECommerce.ServiceLibrary.BusinessLogic;
using Homesite.Diagnostics.ECommerce;
using Homesite.ECommerce.RuleEngine;
using System.Globalization;
using Homesite.IQuote.LookupBroker;
using System.Diagnostics;
using Homesite.ECommerce.ServiceLibrary.Cache;
using System.Xml.Linq;
using Address = Homesite.ECommerce.IQuote.Address;
using ValidationRequestType = Homesite.ECommerce.ServiceLibrary.ValidationService.ValidationRequestType;
using System.Data;
using System.Security.Cryptography;

/** 
 * QUOTE COMPLETE FLAG:
 * 1 complete 
 * 2 3 4 6 dnq
 * 5 incomplete (we shoudl set this on saves before addt'l info save)
 * 0 legitimate & incomplete but shouldn't be used
**/


namespace Homesite.ECommerce.ServiceLibrary.Legacy
{
    public sealed partial class ConsumerWebBusinessLogic : Homesite.ECommerce.Context.IBusinessLogic, IBusinessLogicVisitable
    {
        private QuoteHeader qHeader;
        private QuoteData qData;
        private int _ratingId;

        string ErrorMessage = string.Empty;
        string InfoMessage = string.Empty;


        private bool _isIneligible;
        private const int OlsStatusEmailVerified = 1;
        private const int OlsStatusPaperlessStoredId = 2;
        private const int OlsStatusEmailValidated = 3;
        private const int OlsStatusPasswordVerified = 4;
        private const int OlsStatusReadyToEnroll = 5;
        private const int OlsStatusEmailAlreadyEnrolled = -1;
        private const int OlsStatusPaperlessStoredIdNegative = -2;
        private const int OlsStatusEmailValidatedFailed = -3;

        /// <summary>
        /// Because a Policy Number is created and stored in the database (HomesiteWeb..hs_quote) when the purchase button is clicked but before
        /// the purchase is successful, we cannot use only the existence of a Policy Number as indication of a purchased policy.  QuoteStatus of
        /// 6 will indicate a successful purchase.  Both will be checked in order to verify that this quote has a purchased policy.
        /// Ref. ITR#9394
        /// 4 is Early Cash successful and also indicates purchased policy while Batch is running
        /// </summary>
        public bool IsPurchased
        {
            get
            {
                return (((this.BusinessLogic().Quote.QuoteStatus == 4) || (this.BusinessLogic().Quote.QuoteStatus == 6))
                    && (String.IsNullOrEmpty(this.BusinessLogic().Quote.CompanysPolicyNumber) == false));
            }
        }

        public bool IsIneligible
        {
            get { return _isIneligible; }
            set
            {
                _isIneligible = value;
                if (_isIneligible)
                {
                    //log event isInelligible set to true
                    LogPublishClient.LogEvent(QHeader, 6);
                }
            }
        }

        private bool _isDnq;

        public bool IsDnq
        {
            get
            {
                if (_isDnq && this.BusinessLogic().Quote.CompleteQuoteFlag != 2)
                {
                    this.BusinessLogic().Quote.CompleteQuoteFlag = 2;
                    this.BusinessLogic().Quote.DNQFlag = true;

                    // Prevent sending quote confirmation emails to DNQ'd customers
                    this.BusinessLogic().PrimaryPerson.PersEmailAddr = "dnq@homesite.com";
                    this.BusinessLogic().Quote.Save();

                    ServiceInterface.CallSaveQuoteService();

                    LogPublishClient.LogEvent(QHeader, 5);
                }

                return _isDnq;
            }
            set
            {
                _isDnq = value;

                if (_isDnq && this.BusinessLogic().Quote.CompleteQuoteFlag != 2)
                {
                    this.BusinessLogic().Quote.CompleteQuoteFlag = 2;
                    this.BusinessLogic().Quote.DNQFlag = true;

                    // Prevent sending quote confirmation emails to DNQ'd customers
                    this.BusinessLogic().PrimaryPerson.PersEmailAddr = "dnq@homesite.com";
                    this.BusinessLogic().Quote.Save();

                    ServiceInterface.CallSaveQuoteService();

                    LogPublishClient.LogEvent(QHeader, 5);
                }
            }
        }

        public bool IsMpq
        {
            get { return false; }
        }

        /// <summary>
        /// Private variable ContactCustomerCare
        /// </summary>
        private bool _isContactCustomerCare;
        /// <summary>
        /// Gets or sets a value indicating whether this instance is Contact Customer Care.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance is ContactCustomerCare; otherwise, <c>false</c>.
        /// </value>
        public bool IsContactCustomerCare
        {
            get
            {
                return _isContactCustomerCare;
            }
            set
            {
                _isContactCustomerCare = value;
                if (_isContactCustomerCare)
                {
                    this.BusinessLogic().Quote.Save();
                    ServiceInterface.CallSaveQuoteService();
                    int eventid;
                    switch (this.BusinessLogic().Quote.CompleteQuoteFlag)
                    {
                        case 5:
                            eventid = (int) QuoteEventLogStatus.Incomplete;
                            break;
                        case 1:
                            eventid = (int)QuoteEventLogStatus.Complete;
                            break;
                        default:
                            eventid = 0;
                            break;
                    }
                    LogPublishClient.LogEvent(QHeader, eventid);
                }
            }
        }

        public bool IsAddressVerified
        {
            get
            {
                return this.BusinessLogic().Quote.AddressValidatedFlag ?? 0 == 1;
            }
            set
            {
                this.BusinessLogic().Quote.AddressValidatedFlag = value;
                this.BusinessLogic().Quote.Save();
            }
        }

        bool displayedSSN;
        bool displaySSNModal;
        bool displayAPlusModal;

        public AjaxPanel CurrentAjaxPanel
        {
            get;
            set;
        }
        public AjaxPanel PreviousAjaxPanel
        {
            get;
            set;
        }
        public AjaxPanel NextAjaxPanel
        {
            get;
            set;
        }

        public List<string> QuoteSteps
        {
            get;
            set;
        }

        public bool AffinityAutoDiscountApplied { get; set; }

        private Person primaryPerson;
        private Person secondaryPerson;
        private People people;
        private Addresses addresses;
        private Address propertyAddress;
        private Address mailingAddress;
        private Address priorAddress;
        private Address userMailingAddress;
        private Address userPropertyAddress;
        private Coverage coverage;
        private AdditionalCoverage additionalCoverage;
        private Header header;
        private Quote quote;
        private Structure structure;
        private AvailableCredit credit;
        private Animals animals;
        private Claims claims;
        private ABTestGroupCollection abTestGroups;
        private AddressBrokerOutputs addressBrokerOutputs;
        private AddressBrokerOutput propertyAddressBrokerOutput;
        private PaymentDetail payment;
        private CreditCard creditCard;
        private EFTs eFTs;
        private Endorsements endorsements;
        private FaxDetails faxDetails;
        private HO0448s hO0448s;
        private HomeDayCare homeDayCare;
        private Mortgages mortgages;
        private ScheduledPersonalPropreties scheduledPersonalPropreties;
        private ThankYouDetail thankYouDetail;

        private FloorTypes floorTypes;
        private WallTypes wallTypes;
        private FireplaceTypes fireplaceTypes;
        private QualityGroups qualityGroup;
        private HomeBusinessDetail homeBusinessDetail;
        private AutoClaims autoClaims;

        private List<ControlDataSource> controlData;
        public List<ControlDataSource> ControlData
        {
            get { return controlData; }
        }


        private BusinessLogicSessionDataBinder DataBinder
        {
            get;
            set;
        }
        private BusinessLogicDNQInterface DNQInterface
        {
            get;
            set;
        }
        private BusinessLogicCustCareProcessor ContactCustomerCareProcessor
        {
            get;
            set;
        }
        private BusinessLogicServiceInterface ServiceInterface
        {
            get;
            set;
        }
        private BusinessLogicPurchaseProcessor PurchaseProcessor
        {
            get;
            set;
        }
        private BusinessLogicABTestInterface ABTestInterface
        {
            get;
            set;
        }
        public BusinessLogicMultirateProcessor MultiRateProcessor { get; set; }

        private bool validatedZipForRetrieve;

        public IQuoteConfiguration PartnerConfiguration;

        public string Addr1;
        public string Addr2;
        public string City;
        public string State;
        public string Zip;
        
        #region going away.

        private ConsumerWebSteps CurrentStep { get; set; }


        #endregion

        #region CONSTRUCTORS
        public ConsumerWebBusinessLogic(QuoteHeader qHeader)
        {
            this.qHeader = qHeader;
            this.controlData = new List<ControlDataSource>();

            DataBinder = new BusinessLogicSessionDataBinder();
            DNQInterface = new BusinessLogicDNQInterface();
            ServiceInterface = new BusinessLogicServiceInterface();
            PurchaseProcessor = new BusinessLogicPurchaseProcessor();
            ABTestInterface = new BusinessLogicABTestInterface();
            ContactCustomerCareProcessor = new BusinessLogicCustCareProcessor();
            MultiRateProcessor = new BusinessLogicMultirateProcessor(ServiceInterface, false);

            this.Accept(DataBinder);
            this.Accept(DNQInterface);
            this.Accept(ServiceInterface);
            this.Accept(PurchaseProcessor);
            this.Accept(ABTestInterface);
            this.Accept(ContactCustomerCareProcessor);
            this.Accept(MultiRateProcessor);
        }

        public ConsumerWebBusinessLogic(QuoteHeader qHeader, IQuoteConfiguration config)
        {
            this.qHeader = qHeader;
            this.controlData = new List<ControlDataSource>();

            DataBinder = new BusinessLogicSessionDataBinder();
            DNQInterface = new BusinessLogicDNQInterface();
            ServiceInterface = new BusinessLogicServiceInterface();
            PurchaseProcessor = new BusinessLogicPurchaseProcessor();
            ABTestInterface = new BusinessLogicABTestInterface();
            ContactCustomerCareProcessor = new BusinessLogicCustCareProcessor();
            MultiRateProcessor = new BusinessLogicMultirateProcessor(ServiceInterface, false);
            PartnerConfiguration = config;

            this.Accept(DataBinder);
            this.Accept(DNQInterface);
            this.Accept(ServiceInterface);
            this.Accept(PurchaseProcessor);
            this.Accept(ABTestInterface);
            this.Accept(ContactCustomerCareProcessor);
            this.Accept(MultiRateProcessor);
        }

        public ConsumerWebBusinessLogic(QuoteHeader qHeader, QuoteData qData)
            : this(qHeader)
        {
            this.qData = qData;
        }
        #endregion




        #region PROCESS PAGE
        public string ProcessPage()
        {
            //legacy logging port:
            var configuration = IQuoteConfiguration.GetConfigurationInstance();
            var session = new SessionProvider();

            // Adding below line to clear previous error
            this.ErrorMessage = string.Empty;

            DoPostLogging(session, configuration);

            if (string.IsNullOrEmpty(this.BusinessLogic().QHeader.SessionHash))
            {
                this.BusinessLogic().QHeader.SessionHash = FnvHash.GetHash(this.BusinessLogic().QHeader.SessionId.ToString(), 7).ToHexString();
            }

            if (this.BusinessLogic().QHeader.SessionId <= 0)
            {
                this.BusinessLogic().QHeader.SessionId = ServiceInterface.CallCreateNewSession();
                System.Diagnostics.Debug.WriteLine("[ConsumerWebBusinessLogic] Got session id: " + this.BusinessLogic().QHeader.SessionId);
            }

            this.Log("ProcessPage - Step: " + this.BusinessLogic().QHeader.Step + "... Inspection Result value = " + this.BusinessLogic().Quote.InspectionResult.ToString());

            switch (this.BusinessLogic().QHeader.Step)
            {
                case "Welcome":
                    ProcessWelcome();
                    break;
                case "Coverage":
                    ProccessCoverages();
                    break;
                case "Condo Information":
                    ProcessCondoInformation();
                    break;
                case "APlusClaim":
                    ProcessAPlus();
                    break;
                case "Form2":
                    DataBinder.QuoteDataToSessionDatabase();
                    ProcessForm2Data();
                    break;
                default:
                    switch (this.BusinessLogic().QHeader.FormCode)
                    {
                        case 3: //HO3 - Homeowner
                            ProcessPageH03();
                            break;
                        case 4: //HO4 - Renter
                            ProcessPageH04();
                            break;
                        case 6: //HO6 - Condo
                            ProcessPageH06();
                            break;
                    }
                    break;
            }

            return string.Empty;
        }

        private void DoPostLogging(SessionProvider session, IIQuoteConfiguration configuration)
        {
            var logPost = false;
            Boolean.TryParse(configuration.Setting("LogPOST"), out logPost);
            if (logPost)
            {
                session.LogExperience(this.QHeader.SessionId,
                                      TranslateStepForOldSessionExperience(QHeader.Step),
                                      this.QHeader.FormCode.ToString(), DateTime.Now,
                                      "POST", true, "");
            }
        }


        private void ProcessPageH04()
        {
            if (QHeader.H04Ui_V2)
            {
                // new renters flow
                switch (this.BusinessLogic().QHeader.Step)
                {
                    case "About You":
                        ProcessAboutYouH04UiV2();
                        break;
                }
            }
            else
            {
                // legacy renters flow
                switch (this.BusinessLogic().QHeader.Step)
                {
                    case "About You":
                        ProcessAboutYouH04();
                        break;

                    case "Property Info":
                        ProcessPropertyInfoH04();
                        break;

                    case "Additional Info":
                        decimal? premiumAmt = this.BusinessLogic().Quote.PolicyPremiumAmt;

                        this.DataBinder.QuoteDataToSessionDatabase();

                        if (QHeader.State == "CT")
                        {
                            this.DNQInterface.CallAddressDNQs();
                            if (this.IsDnq) return;
                        }

                        this.DNQInterface.CallPostCreditDNQ();

                        if (!this.IsDnq)
                        {
                            this.ServiceInterface.CallDangerousDogCoverageDefaults();

                            //if (this.BusinessLogic().QHeader.IsMultiRate)
                            //{
                            //    UpdateWindHailOrHurricaneValues(this.BusinessLogic().Coverage.Deductible.Trim());
                            //}

                            this.ServiceInterface.CallRatingService();

                            // save the default premium
                            this.BusinessLogic().Quote.DefaultPremium = this.BusinessLogic().Quote.DefaultPremium ?? (int)this.BusinessLogic().Quote.PolicyPremiumAmt;
                            this.BusinessLogic().Quote.DefaultRatingCaseNumber = this.BusinessLogic().Quote.DefaultRatingCaseNumber ?? int.Parse(this.BusinessLogic().Quote.RatingCaseNumber);
                            this.BusinessLogic().Quote.Save();

                            OldFlowAdditionalInfoLogging(premiumAmt);

                            if (MultiRateProcessor.IsEnabled())
                            {
                                this.Log("Calling ProcessMultiRateForm4..");
                                MultiRateProcessor.ProcessMultiRateForm4();
                            }

                            this.ServiceInterface.CallSaveQuoteService();
                        }

                        break;
                }
            }
        }

        private void ProcessPropertyInfoH04()
        {
            decimal? premiumAmt = this.BusinessLogic().Quote.PolicyPremiumAmt;

            this.DataBinder.QuoteDataToSessionDatabase();
            this.ServiceInterface.CallCreditServiceAsynch();

            this.ServiceInterface.WaitForCreditService();
            this.ServiceInterface.ProcessCreditResults();
            //this.DNQInterface.CallPostCreditDNQ();

            // ** NF Moved here from AboutYou by JG 10/4/10. Rating cannot be done until we ask questions like Number of Occupants
            if (!IsDnq)
            {


                this.ServiceInterface.CallRatingService();
                if ((this.BusinessLogic().Quote.PolicyPremiumAmt == null || this.BusinessLogic().Quote.PolicyPremiumAmt == 0))
                {
                    this.IsIneligible = true;
                    this.NextAjaxPanel = AjaxPanel.InEligible;
                    this.BusinessLogic().Quote.DNQReasons = "Policy premium amount is $0";
                    this.BusinessLogic().Quote.Save();
                }
                else
                {
                    this.BusinessLogic().Quote.DefaultPremium = this.BusinessLogic().Quote.DefaultPremium ?? (int)this.BusinessLogic().Quote.PolicyPremiumAmt;
                    this.BusinessLogic().Quote.DefaultRatingCaseNumber = this.BusinessLogic().Quote.DefaultRatingCaseNumber ?? int.Parse(this.BusinessLogic().Quote.RatingCaseNumber);
                    this.BusinessLogic().Quote.Save();

                    OldFlowPropertyInfoLogging(premiumAmt);

                    // We can't save Asynch in the fee states b/c we need horizon to calc state fees.
                    if (QuoteServices.IsFeeState(qHeader.State))
                    {
                        this.ServiceInterface.CallSaveQuoteService();
                    }
                    else
                    {
                        this.ServiceInterface.CallSaveQuoteServiceAsync();
                    }
                }
            }
        }

        private void ProcessAboutYouH04()
        {
            // wtf is this, i dont know
            if (this.qHeader.State.Trim().ToUpper() == "MD" && this.QData.homeDayCare != null)
            {
                this.QData.businessOnPremises = this.QData.homeDayCare == YesNo.Yes;
            }

            // hate you, last_pagevisit_id.
            if (this.BusinessLogic().Header.Last_PageVisit_ID.HasValue == false || this.BusinessLogic().Header.Last_PageVisit_ID != 6)
            {
                this.BusinessLogic().Header.Last_PageVisit_ID = 5;
                this.BusinessLogic().Header.Save();
            }

            // Check for changed address before saving new data.
            IsAddressVerified = (IsAddressVerified && !(UserChangedAddress()));

            this.DataBinder.QuoteDataToSessionDatabase();

            this.InitializeQuoteHeader();

            if (!IsAddressVerified || (IsAddressVerified && UserChangedAddress()))
            {
                ServiceInterface.CallAddressBrokerService();

                var isExactMatch = ServiceInterface.IsExactMatch(
                    this.BusinessLogic().PropertyAddressBrokerOutput.MatchCode,
                    this.BusinessLogic().PropertyAddressBrokerOutput.LocationQualityCode);

                IsAddressVerified = isExactMatch;

                if (!IsAddressVerified)
                    return;
            }

            this.DNQInterface.CallAddressDNQs();

            if (IsDnq)
            {
                return;
            }

            if (IsIneligible)
            {
                this.IsDnq = false;
                this.IsIneligible = !this.IsDnq;
                return;
            }

            this.DNQInterface.CallCallCenterValidateState();

            this.DataBinder.SaveDefaultAddress();

            this.ServiceInterface.CallProtectionService();

            //if (this.QHeader.H04Ui_V2 || this.QHeader.H06Ui_V2)
            //{
            //    this.ServiceInterface.CallCreditServiceAsynch();
            //}

            this.ServiceInterface.FetchRentersMRP();

            this.ServiceInterface.CallDefaultService();
            this.ServiceInterface.CallDangerousDogCoverageDefaults();
            this.DataBinder.PopulateAvailableCredit();
            this.ServiceInterface.SetCoverageDefaultsH04();


            this.DNQInterface.CallCheckMarketValueForUnsolicitedBusinessDNQ();

            // ITR#7131
            this.DNQInterface.CallCheckMinReplacementCostPerSqFtDNQ();

            if (!SkipGamingCheck)
                ServiceInterface.CallCheckGaming();

            if (IsIneligible) return;

            this.ServiceInterface.CallSaveQuoteServiceAsync();

            // This is all code associated to Walmart associates including the wait for save. 
            // This may add some time to NC and RI for H04 but it is the only way to assure we have a 
            // CompanyQuoteNumber. If the partner is not walmart associate we do not wait so only walmart is affected by this.
            // ProcessOLSEnrollmentQuestions fails with no companyQuoteNumber.
            if (this.BusinessLogic().QHeader.PartnerId.Equals(2319))
            {
                this.ServiceInterface.WaitForSave();

                // if Email is verified, storeID is filled, and agreed to be paperless
                if (this.BusinessLogic().QHeader.OLSStatus == OlsStatusEmailVerified &&
                    !string.IsNullOrEmpty(this.BusinessLogic().QData.storeID) &&
                    this.BusinessLogic().QData.paperless == true)
                {
                    this.BusinessLogic().QHeader.OLSStatus = OlsStatusPaperlessStoredId; // Email validated, storedID filled, paperless yes
                }
                else if (string.IsNullOrEmpty(this.BusinessLogic().QData.storeID) ||
                         this.BusinessLogic().QData.paperless == false)
                {
                    this.BusinessLogic().QHeader.OLSStatus = OlsStatusPaperlessStoredIdNegative;
                }

                ProcessOLSEnrollmentQuestions(QHeader.QuoteNumber, this.BusinessLogic().QData.storeID,
                                              (bool)this.BusinessLogic().QData.paperless);
            }
        }

        private void ProcessAboutYouH06()
        {
            
            // hate you, last_pagevisit_id.
            if (this.BusinessLogic().Header.Last_PageVisit_ID.HasValue == false || this.BusinessLogic().Header.Last_PageVisit_ID != 6)
            {
                this.BusinessLogic().Header.Last_PageVisit_ID = 5;
                this.BusinessLogic().Header.Save();
            }

            // Check for changed address before saving new data.
            IsAddressVerified = (IsAddressVerified && !(UserChangedAddress()));

            DataBinder.QuoteDataToSessionDatabase();

            InitializeQuoteHeader();

            bool userChangedAddress = UserChangedAddress();

            if (!IsAddressVerified || (IsAddressVerified && userChangedAddress))
            {
                ServiceInterface.CallAddressBrokerService();

                var isExactMatch = ServiceInterface.IsExactMatch(
                    this.BusinessLogic().PropertyAddressBrokerOutput.MatchCode,
                    this.BusinessLogic().PropertyAddressBrokerOutput.LocationQualityCode);

                IsAddressVerified = isExactMatch;

                if (!IsAddressVerified)
                    return;
            }

            DNQInterface.CallAddressDNQs();

            if (IsDnq)
            {
                return;
            }

            if (IsIneligible)
            {
                IsDnq = false;
                IsIneligible = !IsDnq;
                return;
            }

            DNQInterface.CallCallCenterValidateState();

            DataBinder.SaveDefaultAddress();

            ServiceInterface.CallProtectionService();

            ServiceInterface.CallDefaultService();
            ServiceInterface.CallDangerousDogCoverageDefaults();
            DataBinder.PopulateAvailableCredit();
            ServiceInterface.SetCoverageDefaultsH06();


            DNQInterface.CallCheckMarketValueForUnsolicitedBusinessDNQ();
            // ITR#7131
            DNQInterface.CallCheckMinReplacementCostPerSqFtDNQ();

            if (!SkipGamingCheck)
            {
                ServiceInterface.CallCheckGaming();
            }

            if (IsIneligible) return;


            ServiceInterface.CallSaveQuoteServiceAsync();
        }


        private void ProcessAboutYouH04UiV2()
        {
            if (this.qHeader.State.Trim().ToUpper() == "MD" && this.QData.homeDayCare != null)
            {
                this.QData.businessOnPremises = this.QData.homeDayCare == YesNo.Yes;
            }
            // Check for changed address before saving new data.
            IsAddressVerified = (IsAddressVerified && !(UserChangedAddress()));

            DataBinder.BindSessionToQuoteStartData();

            DataBinder.QuoteDataToSessionDatabase();

            // this adds the discount for walmart because we need to make it easier for them...
            if (ProcessWalmartFlow() && this.BusinessLogic().QHeader.MarketingId.ToLower().Equals("online"))
            {
                this.BusinessLogic().Quote.AutoPolicyNumber = "WalmartDotCom";
                this.BusinessLogic().Quote.PartnerAutoPolicy = 1;
                this.BusinessLogic().Quote.Save();
            }

            if (!IsAddressVerified || (IsAddressVerified && UserChangedAddress()))
            {
                ServiceInterface.CallAddressBrokerService();

                var isExactMatch = ServiceInterface.IsExactMatch(
                    this.BusinessLogic().PropertyAddressBrokerOutput.MatchCode,
                    this.BusinessLogic().PropertyAddressBrokerOutput.LocationQualityCode);

                IsAddressVerified = isExactMatch;

                if (!IsAddressVerified)
                    return;
            }

            DNQInterface.CallAddressDNQs();

            if (!IsDnq)
                DNQInterface.CallCallCenterValidateState();


            // ITR#8398 - FL Reentry : Get BCEG code.
            if (QHeader.State.Equals("FL"))
            {
                DataBinder.SaveBCEG();
            }

            if (IsDnq)
            {
                IsIneligible = !IsDnq;
            }
            else if (IsIneligible)
            {
                IsDnq = false;
            }
            else
            {
                DataBinder.SaveDefaultAddress();

                ServiceInterface.CallProtectionService();

                ServiceInterface.CallCreditServiceAsynch();

                ServiceInterface.FetchRentersMRP();

                // set defaults
                ServiceInterface.CallDefaultService();
                ServiceInterface.CallDangerousDogCoverageDefaults();
                DataBinder.PopulateAvailableCredit();
                ServiceInterface.SetCoverageDefaultsH04();


                DNQInterface.CallCheckMarketValueForUnsolicitedBusinessDNQ();
                // ITR # 7131
                DNQInterface.CallCheckMinReplacementCostPerSqFtDNQ();

                if (!SkipGamingCheck)
                {
                    ServiceInterface.CallCheckGaming();
                }

                if (IsIneligible) return;

                // process credit
                ServiceInterface.WaitForCreditService();
                ServiceInterface.ProcessCreditResults();
                DNQInterface.CallPostCreditDNQ();

                if (IsDnq) return;

                var premiumAmt = this.BusinessLogic().Quote.PolicyPremiumAmt;
                bool stripOffDiscount = false;

                if (this.BusinessLogic().QHeader.PartnerId.Equals(2319) && this.QHeader.OLSStatus != OlsStatusEmailValidated)
                {
                    stripOffDiscount = true;
                    UpdateDefaultWalmartDiscount("ASSOCIATE15");
                }
                else if (this.BusinessLogic().QHeader.PartnerId.Equals(2319) && this.QHeader.OLSStatus == OlsStatusEmailValidated && this.BusinessLogic().Quote.CompanysQuoteNumber != null)
                {
                    DataTable tb = DirectWebDAC.GetOLSEnrollmentValues(this.BusinessLogic().Quote.CompanysQuoteNumber);
                    // check to make sure the data is not empty or null
                    if (tb != null && tb.Rows.Count > 0)
                    {
                        UpdateDefaultWalmartDiscount((string)tb.Rows[0]["EMailVerificationCode"]);
                    }
                    else
                    {
                        stripOffDiscount = true;
                        UpdateDefaultWalmartDiscount("ASSOCIATE15");
                    }
                }

                //if (this.BusinessLogic().QHeader.IsMultiRate)
                //{
                //    UpdateWindHailOrHurricaneValues(this.BusinessLogic().Coverage.Deductible.Trim());
                //}

                ServiceInterface.CallRatingService();

               
                if ((this.BusinessLogic().QHeader.PartnerId.Equals(2319) && this.QHeader.OLSStatus != OlsStatusEmailValidated) || stripOffDiscount)
                {
                    UpdateDefaultWalmartDiscount(string.Empty);
                }


                if ((this.BusinessLogic().Quote.PolicyPremiumAmt == null || this.BusinessLogic().Quote.PolicyPremiumAmt == 0))
                {
                    IsIneligible = true;
                    NextAjaxPanel = AjaxPanel.InEligible;

                    this.BusinessLogic().Quote.DNQReasons = "Policy premium amount is $0";
                    this.BusinessLogic().Quote.Save();
                }
                else
                {

                    var premium = Convert.ToInt32(this.BusinessLogic().Quote.PolicyPremiumAmt);
                    this.BusinessLogic().Quote.DefaultPremium = this.BusinessLogic().Quote.DefaultPremium ?? premium;
                    this.BusinessLogic().Quote.DefaultRatingCaseNumber = this.BusinessLogic().Quote.DefaultRatingCaseNumber ?? int.Parse(this.BusinessLogic().Quote.RatingCaseNumber);
                    if (MultiRateProcessor.IsEnabled())
                    {
                        this.Log("Calling ProcessMultiRateForm4..");
                        MultiRateProcessor.ProcessMultiRateForm4();
                    }

                    ServiceInterface.CallSaveQuoteService();
                    DirectWebDAC.InsertTrackingId(this.BusinessLogic().QHeader.QuoteNumber, this.BusinessLogic().QHeader.TrackingId);

                    if (ProcessWalmartFlow())
                    {
                        SetWalmartSpecificHeaderFields();
                    }

                    if (this.BusinessLogic().QHeader.PartnerId.Equals(2319))
                    {
                        // if Email is verified, storeID is filled, and agreed to be paperless
                        if (this.BusinessLogic().QHeader.OLSStatus == OlsStatusEmailVerified &&
                            !string.IsNullOrEmpty(this.BusinessLogic().QData.storeID) &&
                            this.BusinessLogic().QData.paperless == true)
                        {
                            this.BusinessLogic().QHeader.OLSStatus = OlsStatusPaperlessStoredId;
                            // Email validated, storedID filled, paperless yes
                        }
                        else if (string.IsNullOrEmpty(this.BusinessLogic().QData.storeID) ||
                                 this.BusinessLogic().QData.paperless == false)
                        {
                            this.BusinessLogic().QHeader.OLSStatus = OlsStatusPaperlessStoredIdNegative;
                        }
                        ProcessOLSEnrollmentQuestions(QHeader.QuoteNumber, QData.storeID, QData.paperless);
                    }
                    LogConsumerEvents(premiumAmt);
                }
            }
        }


        private void ProcessPageH06()
        {
            decimal? premiumAmt;
            switch (this.BusinessLogic().QHeader.Step)
            {
                case "About You": // P1 -> P2 Transition
                    ProcessAboutYouH06();
                    break;

                case "Property Info": // P3 -> P4 Transition

                    premiumAmt = this.BusinessLogic().Quote.PolicyPremiumAmt;

                    this.DataBinder.QuoteDataToSessionDatabase();
                    this.ServiceInterface.CallCreditServiceAsynch();
                    this.DNQInterface.CallCheckMarketValueForUnsolicitedBusinessDNQ();
                    // ITR#7131
                    this.DNQInterface.CallCheckMinReplacementCostPerSqFtDNQ();

                    this.DataBinder.PopulateAvailableCredit();

                    if (!SkipGamingCheck)
                    {
                        this.ServiceInterface.CallCheckGaming();
                    }

                    if (IsIneligible) return;

                    this.ServiceInterface.WaitForCreditService();
                    this.ServiceInterface.ProcessCreditResults();

                    if (!IsDnq)
                    {
                        this.ServiceInterface.CallRatingService();

                        if ((this.BusinessLogic().Quote.PolicyPremiumAmt == null || this.BusinessLogic().Quote.PolicyPremiumAmt == 0))
                        {
                            this.IsIneligible = true;
                            this.NextAjaxPanel = AjaxPanel.InEligible;
                            this.BusinessLogic().Quote.DNQReasons = "Policy premium amount is $0";
                            this.BusinessLogic().Quote.Save();
                        }
                        else
                        {

                            this.BusinessLogic().Quote.DefaultPremium = this.BusinessLogic().Quote.DefaultPremium ?? (int)this.BusinessLogic().Quote.PolicyPremiumAmt;
                            this.BusinessLogic().Quote.DefaultRatingCaseNumber = this.BusinessLogic().Quote.DefaultRatingCaseNumber ?? int.Parse(this.BusinessLogic().Quote.RatingCaseNumber);
                            this.BusinessLogic().Quote.Save();

                            OldFlowPropertyInfoLogging(premiumAmt);

                            if (QuoteServices.IsFeeState(qHeader.State))
                            {
                                this.ServiceInterface.CallSaveQuoteService();
                            }
                            else
                            {
                                this.ServiceInterface.CallSaveQuoteServiceAsync();
                            }
                        }
                    }

                    break;

                case "Additional Info": //P2->P3 transition

                    premiumAmt = this.BusinessLogic().Quote.PolicyPremiumAmt;

                    this.DataBinder.QuoteDataToSessionDatabase();

                    if (QHeader.State == "CT")
                    {
                        this.DNQInterface.CallAddressDNQs();
                        if (this.IsDnq) return;
                    }

                    this.DNQInterface.CallPostCreditDNQ();

                    if (!this.IsDnq)
                    {
                        this.ServiceInterface.CallDangerousDogCoverageDefaults();
                        this.ServiceInterface.CallRatingService();



                        // save the default premium
                        this.BusinessLogic().Quote.DefaultPremium = this.BusinessLogic().Quote.DefaultPremium ?? (int)this.BusinessLogic().Quote.PolicyPremiumAmt;
                        this.BusinessLogic().Quote.DefaultRatingCaseNumber = this.BusinessLogic().Quote.DefaultRatingCaseNumber ?? int.Parse(this.BusinessLogic().Quote.RatingCaseNumber);
                        int a = (int)this.BusinessLogic().Quote.DefaultPremium;
                        int b = (int)this.BusinessLogic().Quote.PolicyPremiumAmt;
                        this.BusinessLogic().Quote.Save();

                        OldFlowAdditionalInfoLogging(premiumAmt);

                        this.ServiceInterface.CallSaveQuoteService();

                    }

                    break;
            }
        }


        private void ProcessCondoInformation()
        {
          

            // hate you, last_pagevisit_id.
            if (this.BusinessLogic().Header.Last_PageVisit_ID.HasValue == false || this.BusinessLogic().Header.Last_PageVisit_ID != 6)
            {
                this.BusinessLogic().Header.Last_PageVisit_ID = 5;
                this.BusinessLogic().Header.Save();
            }

            // Check for changed address before saving new data.
            IsAddressVerified = (IsAddressVerified && !(UserChangedAddress()));

            DataBinder.QuoteDataToSessionDatabase();

            InitializeQuoteHeader();

            if (!IsAddressVerified || (IsAddressVerified && UserChangedAddress()))
            {
                ServiceInterface.CallAddressBrokerService();

                var isExactMatch = ServiceInterface.IsExactMatch(
                    this.BusinessLogic().PropertyAddressBrokerOutput.MatchCode,
                    this.BusinessLogic().PropertyAddressBrokerOutput.LocationQualityCode);

                IsAddressVerified = isExactMatch;

                if (!IsAddressVerified)
                    return;
            }

            DNQInterface.CallAddressDNQs();
            DNQInterface.CallCallCenterValidateState();

            if (IsDnq)
            {
                IsIneligible = !IsDnq;
                return;
            }

            if (IsIneligible)
            {
                IsDnq = false;
                return;
            }


            DataBinder.SaveDefaultAddress();

            ServiceInterface.CallProtectionService();

            ServiceInterface.CallCreditServiceAsynch();

            // various defaults...
            ServiceInterface.CallDefaultService();
            ServiceInterface.CallDangerousDogCoverageDefaults();

            ServiceInterface.SetCoverageDefaultsH06();
            DataBinder.PopulateAvailableCredit();

            DNQInterface.CallCheckMarketValueForUnsolicitedBusinessDNQ();
            // ITR#7131
            DNQInterface.CallCheckMinReplacementCostPerSqFtDNQ();

            if (!SkipGamingCheck) // for unit testing
            {
                ServiceInterface.CallCheckGaming();
            }

            if (IsDnq)
            {
                IsDnq = true;
                return;
            }

            if (IsIneligible)
            {
                IsDnq = false;
                return;
            }

            ServiceInterface.WaitForCreditService();
            ServiceInterface.ProcessCreditResults();
            DNQInterface.CallPostCreditDNQ();

            // ITR#8398 - FL Reentry : Get BCEG code.
            if (!IsDnq)
            {
                if (QHeader.State.Equals("FL"))
                {
                    DataBinder.SaveBCEG();
                }
            }

            if (!IsDnq)
            {
                decimal? premiumAmt = this.BusinessLogic().Quote.PolicyPremiumAmt;

                ServiceInterface.CallRatingService();

                if ((this.BusinessLogic().Quote.PolicyPremiumAmt == null || this.BusinessLogic().Quote.PolicyPremiumAmt == 0))
                {
                    Log("Zero premium!");
                    IsIneligible = true;
                    NextAjaxPanel = AjaxPanel.InEligible;
                    this.BusinessLogic().Quote.DNQReasons = "Policy premium amount is $0";
                    this.BusinessLogic().Quote.Save();
                }
                else
                {

                    this.BusinessLogic().Quote.DefaultPremium = this.BusinessLogic().Quote.DefaultPremium ?? (int)this.BusinessLogic().Quote.PolicyPremiumAmt;
                    this.BusinessLogic().Quote.DefaultRatingCaseNumber = this.BusinessLogic().Quote.DefaultRatingCaseNumber ?? int.Parse(this.BusinessLogic().Quote.RatingCaseNumber);
                    this.BusinessLogic().Quote.Save();

                    ServiceInterface.CallSaveQuoteService();

                    DirectWebDAC.InsertTrackingId(this.BusinessLogic().QHeader.QuoteNumber, this.BusinessLogic().QHeader.TrackingId);

                    LogConsumerEvents(premiumAmt);

                }
            }
        }

        private void ProcessPageH03()
        {
            bool isStepBeforeCoverages = false;
            isStepBeforeCoverages = IsStepBeforeCoverages();

            if (!isStepBeforeCoverages)
            {
                switch (this.BusinessLogic().QHeader.Step)
                {
                    case "Your Address":
                        this.LogInvocation(ProcessQuoteStart);
                        break;
                    case "Coverage":                        
                        this.LogInvocation(ProccessCoverages);
                        break;
                    case "Property Info":
                        this.LogInvocation(this.DataBinder.QuoteDataToSessionDatabase);
                        this.DataBinder.SaveDataObjectsToHorisonAsync();
                        break;
                    case "About You":
                        this.LogInvocation(this.DataBinder.QuoteDataToSessionDatabase);
                        this.DataBinder.SaveDataObjectsToHorisonAsync();
                        break;
                    default:
                        this.LogInvocation(this.DataBinder.QuoteDataToSessionDatabase);
                        this.DataBinder.SaveDataObjectsToHorisonAsync();
                        break;
                }
            }
            else
            {
                this.LogInvocation(ProcessQuoteHomeowners);
            }
        }
        #endregion


        #region IBusinessLogic Top-Level Members




        private bool IsStepBeforeCoverages()
        {
            bool isStepBeforeCoverages;
            if (!string.IsNullOrEmpty(this.QHeader.QuoteSteps))
            {

                string[] steps = this.QHeader.QuoteSteps.Split(',');
                isStepBeforeCoverages = steps[(steps.Count() - 2)] == this.QHeader.Step.Replace(' ', '_');
            }
            else
            {
                isStepBeforeCoverages = false;
            }
            return isStepBeforeCoverages;
        }

        //public int GetNewSessionId()
        //{
        //    return this.ServiceInterface.CallCreateNewSession();
        //}

        //public int GetNewSessionId(int partnerID, int formCode)
        //{
        //    return this.ServiceInterface.CallCreateNewSession(partnerID, formCode);
        //}

        public string GetCityForRetrieve()
        {

            if (!validatedZipForRetrieve) // dont make duplicate calls to AB
            {

                this.DataBinder.PropertyAddress.PostalCode = QHeader.Zip;
                bool r = this.ServiceInterface.CallValidateZipCodeService();
                this.validatedZipForRetrieve = true;
            }

            return this.DataBinder.PropertyAddress.City;
        }

        public string GetStateForRetreive()
        {
            if (!validatedZipForRetrieve) // dont make duplicate calls to AB
            {

                this.DataBinder.PropertyAddress.PostalCode = QHeader.Zip;
                bool r = this.ServiceInterface.CallValidateZipCodeService();
                this.validatedZipForRetrieve = true;
            }

            return this.DataBinder.PropertyAddress.StateProvCd;
        }


        public AjaxResponse LoadQuoteFromHorizon(QuoteHeader quoteHeader)
        {
            ErrorMessage = string.Empty;
            InfoMessage = string.Empty;

            var response = new AjaxResponse { quoteHeader = quoteHeader };

            InitializeSessionHeaderForRetrieve(quoteHeader);

            this.QHeader.QualityGroup = QuoteServices.GetQualityGroup(qHeader, this.BusinessLogic().PropertyAddress.PostalCode);

            var quoteStatus = GetCompanysPolicyNumber(quoteHeader);

            //ITR#9394 - When Credit Card Authorization Failed, the user should be able to retrieve the quote
            // to continue to purchase the quote despite having a policy number assigned.
            if (DirectWebDAC.AllowRetrieveAfterDeclinedTransaction(qHeader.QuoteNumber))
            {
                quoteStatus = 0;
            }

            Step nextPageIndex;

            var retrieveStatus = ServiceInterface.CallRetrieveIQuoteService();
            if (quoteStatus != -1 && (retrieveStatus == 0 || retrieveStatus == -8))
            {
                DataBinder.RefreshDataObjects();

                nextPageIndex = InterpretCompleteQuoteFlag(quoteHeader);

                UpdateAgeOfClaimForRetrieve();

                SetCompleteQuoteFlagForRetrieve(quoteHeader);

                this.BusinessLogic().Quote.CompleteQuoteFlag = (short)quoteHeader.CompleteQuoteFlag;

                response.quoteHeader.TrackingId = DirectWebDAC.GetTrackingId(quoteHeader.QuoteNumber);

                DataBinder.SaveDataObjects();

                ServiceInterface.CallValidateAddressServiceForRetrieve();

                // check address dnqs again & make sure horizon didn't dnq this quote
                if (!RetrieveDNQ() && this.BusinessLogic().Quote.CompleteQuoteFlag != 2)
                {
                    RestoreQuoteHeader(response);
                    //ITR#8945 - Check Contact Customer Care Logic & Redirect to Page for complete quote flag.  
                    if (!RetrieveContactCustomerCare())
                    {
                        DataBinder.SaveDefaultAddress();
                        
                        ServiceInterface.CallCreditService();
                        ServiceInterface.CallDefaultService();
                        if(PartnerConfiguration != null)
                        {
                            DataBinder.SaveMarketingInfo(null, PartnerConfiguration);
                           
                        }
                        else
                        {
                            DataBinder.SaveMarketingInfo();
                        }
                       
                        DataBinder.SaveDataObjects();

                        this.BusinessLogic().Quote.PartnerId = qHeader.PartnerId;
                        this.BusinessLogic().Quote.RetrieveFlag = true;
                        this.BusinessLogic().Quote.AddressValidatedFlag = true;
                        this.BusinessLogic().Quote.PolicyEffectiveDate = Convert.ToDateTime(response.quoteHeader.PolicyEffectiveDate);
                        this.BusinessLogic().Quote.Save();

                        CheckExpiredQuote(quoteHeader, ref nextPageIndex);

                        //ITR#9651 Good Driver
                        //Set the AutoClaimsRequestID null in order to make a new call to auto claims service
                        if (quoteHeader.ExpiredQuote)
                        {
                            this.BusinessLogic().PrimaryPerson.AutoClaimsRequestID = null;
                            this.BusinessLogic().PrimaryPerson.Save();
                        }

                        GetUpdatedTRSInfoOnRetrieve();

                        ServiceInterface.CallRatingService();

                        ServiceInterface.FixIPropPhone();

                        if (ControlData.Count > 0)
                        {
                            foreach (var t in ControlData)
                            {
                                response.DataSource.Add(t);
                            }
                        }

                        ControlData.Clear();

                    }
                    else
                    {
                        response.NextPanel = AjaxPanel.ContactCustomerCare;
                        response.RedirectURL = "ContactCustomerCare.aspx";
                    }

                }
                else
                {
                    response.NextPanel = AjaxPanel.DNQ;
                }
            }
            else if (quoteStatus == -1 || retrieveStatus == 1)
            {
                var uiSvc = new UIServices();
                var partnerPhoneNumber = uiSvc.GetPartnerPhoneNumber(quoteHeader.PartnerId);
                 
                nextPageIndex = Step.InitialQuestions1;
                ErrorMessage = "This quote cannot be retrieved at this time.  Please call " + partnerPhoneNumber + " to talk to a licensed " +
                               "Customer Service Person and mention your Quote Number " + this.BusinessLogic().Quote.CompanysQuoteNumber + ".  We look forward to serving you.";
            }
            else // horizon returned a random error code.
            {
                nextPageIndex = Step.InitialQuestions1;
                ErrorMessage = "There was a problem processing your request.  Please try again later.";
            }

            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                if (nextPageIndex == Step.InitialQuestions2)
                {
                    response.NextPanel = AjaxPanel.Your_Address;
                    response.quoteHeader.LandingStep = "Your_Address";
                    response.RedirectURL = "BeginQuote.aspx";
                }
                else if (nextPageIndex == Step.InitialQuestions1)
                {
                    response.RedirectURL = "Welcome.aspx";
                }
                else
                {
                    response.RedirectURL = "BeginQuote.aspx";
                }

                response.errorMessage = ErrorMessage;
                response.errorOccured = true;
            }
            else
            {
                AjaxPanel nextPanel;

                switch (nextPageIndex)
                {
                    case Step.PremiumCustomization:
                        //ITR#8945 - Is ContactCustomerCare
                        if (quoteHeader.FormCode == 3 && IsContactCustomerCare)
                        {
                            nextPanel = AjaxPanel.ContactCustomerCare;
                        }
                        else
                        {
                            nextPanel = AjaxPanel.Coverage;
                        }
                        break;
                    case Step.DNQ:
                        nextPanel = AjaxPanel.DNQ;
                        break;
                    default:
                        if (quoteHeader.FormCode == 3)
                        {
                            nextPanel = AjaxPanel.Your_Address;
                        }
                        else if (quoteHeader.FormCode == 4)
                        {
                            nextPanel = AjaxPanel.About_You;
                        }
                        else if (quoteHeader.H06Ui_V2)
                        {
                            nextPanel = AjaxPanel.Condo_Information;
                        }
                        else if (quoteHeader.H04Ui_V2)
                        {
                            nextPanel = AjaxPanel.About_You;
                        }
                        else
                        {
                            throw new BusinessLogicException("Unknown starting point.");
                        }

                        break;
                }

                response.NextPanel = nextPanel;

            }

            response.quoteHeader.LandingStep = response.NextPanel.ToString();
            return response;
        }


        #region RETRIEVE

        private void GetUpdatedTRSInfoOnRetrieve()
        {
            // only bother for MI Mkt Value
            if (QHeader.State == "MI" && QHeader.FormCode == 3)
            {
                ServiceInterface.CallTaxRetrivalService();
            }

        }

        //private void GetUpdatedTUInfoOnRetrieve()
        //{
        //    //Check to see if TU record available in the rating DB,
        //    Homesite.IQuote.ISOBroker.ISOProvider ISORating = new Homesite.IQuote.ISOBroker.ISOProvider();

        //    int RequestSeqNumber = 0;
        //    string CreditScore = "";
        //    int Noofmatches = 0;

        //    int requestId = this.IQuote().PrimaryPerson.TransUnionRequestID == null ? 0 : (int)this.IQuote().PrimaryPerson.TransUnionRequestID;
        //    //try
        //    //{
        //        // ITR#3855 Product Template 2010 change of signature
        //        // All calls to StoreTUScoreInRatingDB and GetLatestTUInfo are being commented out
        //        // as per Gaurav's directive because the logic has been moved elsewhere
        //        //string reasonCode1 = String.Empty;
        //        //string reasonCode2 = String.Empty;
        //        //string reasonCode3 = String.Empty;
        //        //string reasonCode4 = String.Empty;
        //        //ISORating.GetLatestTUInfo(requestId, out RequestSeqNumber, out CreditScore, out Noofmatches, out reasonCode1, out reasonCode2, out reasonCode3, out reasonCode4);
        //    //}
        //    //catch (Exception ex)
        //    //{
        //    //    this.ErrorMessage = "There was a problem processing your request.  Please try again later.";
        //    //}
        //    if (CreditScore.Trim().Equals(""))
        //        CreditScore = "0";
        //    //try
        //    //{
        //        // ITR#3855 Product Template 2010 change of signature
        //        // All calls to StoreTUScoreInRatingDB and GetLatestTUInfo are being commented out
        //        // as per Gaurav's directive because the logic has been moved elsewhere
        //    //    string reasoncode1 = String.Empty;
        //    //    string reasoncode2 = String.Empty;
        //    //    string reasoncode3 = String.Empty;
        //    //    string reasoncode4 = String.Empty;
        //    //    ISORating.StoreTUScoreInRatingDB(requestId, int.Parse(CreditScore), Noofmatches, reasoncode1, reasoncode2, reasoncode3, reasoncode4);
        //    //}
        //    //catch (Exception ex)
        //    //{
        //    //    this.ErrorMessage = "There was a problem processing your request.  Please try again later.";
        //    //}
        //}

        private static System.Nullable<short> GetCompanysPolicyNumber(QuoteHeader qHeader)
        {
            System.Nullable<short> intQuoteStatus = 0;
            Homesite.IQuote.Data.LookupDataProviderTableAdapters.QueriesTableAdapter qtd =
                new Homesite.IQuote.Data.LookupDataProviderTableAdapters.QueriesTableAdapter();

            qtd.GetCompanysPolicyNumber(qHeader.QuoteNumber, ref intQuoteStatus);
            return intQuoteStatus;
        }

        private void CheckExpiredQuote(QuoteHeader quoteHeader, ref Step nextPageIndex)
        {
            var policyEffectiveDate = DateTime.Parse(this.BusinessLogic().Coverage.PolicyEffDate);
            var expirationDate = (DateTime)this.BusinessLogic().Quote.ExpirationDt;

            var today = DateTime.Today;
            var ts1 = expirationDate - today;
            var ts2 = policyEffectiveDate - today;

            if ((ts1.Days <= 0) || (ts2.Days <= 0))
            {
                var sessionAdapter = new Homesite.IQuote.Data.SessionDataProviderTableAdapters.Session();

                try
                {
                    sessionAdapter.UpdateExpiredQuote(quoteHeader.SessionId);
                }
                catch (Exception ex)
                {
                    throw new SystemException("Error updating expired quote.", ex);
                }

                if (this.BusinessLogic().AdditionalCoverage.Ho0015Flag != null &&
                    this.BusinessLogic().AdditionalCoverage.Ho0015Flag == 1)
                {
                    var hshohhFlagProvider =
                        new Homesite.IQuote.Data.ISODataProviderTableAdapters.TUScoreRateTableAdapter();

                    // RI Mitigation -- added the issue date parameter
                    int? grandFatherFlagHooo15 = 0;
                    int? avlForSelectHooo15 = 0;
                    hshohhFlagProvider.GetHSHOHHFlags(quoteHeader.State, "HO0015", Convert.ToDateTime(quoteHeader.OriginalQuoteDate), ref grandFatherFlagHooo15, ref avlForSelectHooo15);
                    //There is no need to check grandfather flag as it is expired quote
                    if (avlForSelectHooo15 == 1)
                    {
                        this.BusinessLogic().AdditionalCoverage.Ho0015Flag = 1;
                        this.BusinessLogic().AdditionalCoverage.HH0015Flag = 0;
                    }
                    else
                    {
                        this.BusinessLogic().AdditionalCoverage.Ho0015Flag = 0;
                        this.BusinessLogic().AdditionalCoverage.HH0015Flag = 1;
                    }
                }

                if (quoteHeader.State.Equals("CA"))
                    SetCAEndoresementsOnRetrieve(quoteHeader);

                nextPageIndex = Step.InitialQuestions2;
                quoteHeader.ExpiredQuote = true;

                if (qHeader.FormCode == 1)
                {
                    this.BusinessLogic().Header.FormNumber = 4;
                    this.BusinessLogic().Quote.FormNumber = "4";
                    this.BusinessLogic().Header.Save();
                    this.BusinessLogic().Quote.Save();
                }
            }
        }

        private Step InterpretCompleteQuoteFlag(QuoteHeader quoteHeader)
        {
            Step nextPageIndex;

            switch (quoteHeader.CompleteQuoteFlag)
            {
                case 1:
                    /* we are currently legally required to force all CA retrieves
                     * which didn't see the ITV question set back thru the quote flow */
                    if (this.BusinessLogic().QHeader.State.Equals("CA") &&
                        (this.BusinessLogic().QHeader.FormCode == 3) &&
                        (this.BusinessLogic().Structure.FoundationShape == null ||
                        this.BusinessLogic().Structure.FoundationShape == 0))
                    {
                        nextPageIndex = Step.IndicativeQuote;
                    }
                    else
                    {
                        nextPageIndex = Step.PremiumCustomization;
                    }
                    break;
                case 5:
                    nextPageIndex = Step.IndicativeQuote;
                    break;
                case 2:
                    nextPageIndex = Step.DNQ;
                    break;
                default:
                    {
                        UIServices uiSvc = new UIServices();
                        string partnerPhoneNumber = uiSvc.GetPartnerPhoneNumber(quoteHeader.PartnerId);

                        nextPageIndex = Step.InitialQuestions1;
                        this.ErrorMessage = "This quote cannot be retrieved at this time.  Please call " + partnerPhoneNumber + " to talk to a licensed " +
                                            "Customer Service Person and mention your Quote Number " + this.BusinessLogic().Quote.CompanysQuoteNumber + ".  We look forward to serving you.";
                    }
                    break;
            }

            return nextPageIndex;
        }

        private bool RetrieveDNQ()
        {
            DNQInterface.CallAddressDNQs();
            return IsDnq;
        }

        /// <summary>
        /// ITR#8945 - Call HomeBusinessEndorsementLogic on Retrieve to reidrect to Contact Customer Page for Completed Quotes.
        /// </summary>
        /// <returns></returns>
        private bool RetrieveContactCustomerCare()
        {
            if (qHeader.CompleteQuoteFlag == 1 && qHeader.FormCode == 3 && !IsDnq)
            {
                ContactCustomerCareProcessor.ProcessHomeBusinessEndorsementLogic();
            }
            return IsContactCustomerCare;
        }

        private void SetCompleteQuoteFlagForRetrieve(QuoteHeader qHeader)
        {
            #region complete quote flag switching
            // Fix for KMT Ticket 5564
            if (qHeader.CompleteQuoteFlag == 1) //Complete Quote
                this.BusinessLogic().Quote.CompleteQuoteFlag = 1;
            else if (qHeader.CompleteQuoteFlag == 2) //DNQ
                this.BusinessLogic().Quote.CompleteQuoteFlag = 2;
            else if (qHeader.CompleteQuoteFlag == 5) //Incomplete Quote
                this.BusinessLogic().Quote.CompleteQuoteFlag = -1;
            #endregion
        }

        private void UpdateAgeOfClaimForRetrieve()
        {
            # region Bug 2458 / 2157 Age of Claim Fix
            if (this.BusinessLogic().Claims.Count > 0)
            {
                foreach (Claim claim in this.BusinessLogic().Claims)
                {
                    if (claim.DateOfClaim != null)
                        claim.AgeOfClaim = Convert.ToDecimal(DateTime.Now.Year - claim.DateOfClaim.Year);
                }
            }
            #endregion
        }

        private void SetCAEndoresementsOnRetrieve(QuoteHeader qHeader)
        {
            Homesite.IQuote.Data.ISODataProviderTableAdapters.TUScoreRateTableAdapter hshohhFlagProvider = new Homesite.IQuote.Data.ISODataProviderTableAdapters.TUScoreRateTableAdapter();

            // RI Mitigation
            //System.Nullable<byte> grandFatherFlag = 0, avlForSelect = 0;
            System.Nullable<int> grandFatherFlag = 0, avlForSelect = 0;
            hshohhFlagProvider.GetHSHOHHFlags(qHeader.State, "HH0015", Convert.ToDateTime(qHeader.OriginalQuoteDate), ref grandFatherFlag, ref avlForSelect);
            if (avlForSelect == 1)
            {
                this.BusinessLogic().AdditionalCoverage.HH0015Flag = 1;
                this.BusinessLogic().AdditionalCoverage.Ho0015Flag = 0;
                // set flag to 2 in CA table.

                Homesite.IQuote.Data.ISODataProviderTableAdapters.TUScoreRateTableAdapter CAHH0015QueryAdapter = new Homesite.IQuote.Data.ISODataProviderTableAdapters.TUScoreRateTableAdapter();
                CAHH0015QueryAdapter.SetCAPackageQuoteFlag(qHeader.QuoteNumber, null);
            }
        }

        private void InitializeSessionHeaderForRetrieve(QuoteHeader qHeader)
        {
            //BusinessLogicSessionDataBinder data = new BusinessLogicSessionDataBinder();
            //this.Accept<BusinessLogicSessionDataBinder>(data);

            // set up the header in the session DB
            this.BusinessLogic().Header.RetrieveDateOfBirth = qHeader.RetrieveDateOfBirth;
            this.BusinessLogic().Header.RetrieveZipCode = qHeader.Zip;
            this.BusinessLogic().Header.PartnerID = qHeader.PartnerId;
            this.BusinessLogic().Header.QuoteNumber = qHeader.QuoteNumber;
            this.BusinessLogic().Header.isRetrieve = true;
            this.BusinessLogic().Header.ProgramType = 0;
            this.BusinessLogic().Header.UnderwritingNumber = 0;
            this.BusinessLogic().Header.RatingVersion = 0;
            this.BusinessLogic().Header.SubProgramType = 0;
            this.BusinessLogic().Header.RetroFlag = 0;
            this.BusinessLogic().Header.Last_PageVisit_ID = 6;
            this.BusinessLogic().Header.IsApexState = false;
            this.BusinessLogic().Header.IsXWindState = false;
            this.BusinessLogic().Header.Save();
        }

        #endregion

        public bool GetMitigationFlag()
        {
            if (this.BusinessLogic().Header.FormNumber == 3)
            {
                return (DirectWebDAC.GetMitigationFlag(QHeader.State, (DateTime)this.BusinessLogic().Quote.IssueDate, DateTime.Parse(QHeader.PolicyEffectiveDate), QHeader.UnderwritingCompanyNumber, (int)QHeader.ProgramType) == 0) ? false : true;
            }
            else
            {
                return true;
            }
        }

        public AjaxResponse RetrieveQuote(string quoteNumber, DateTime dateOfBirth, string zip, int partnerId)
        {
            throw new Exception("somethings wrong!");
        }

        private void RestoreQuoteHeader(AjaxResponse response)
        {

            var restoredHeader = IQuote.Header.GetHeader(this.BusinessLogic().QHeader.SessionId);

            response.quoteHeader.IsRetrieve = true;

            DateTime tmpEffDate;
            if (this.BusinessLogic().Coverage.PolicyEffDate != null && DateTime.TryParse(this.BusinessLogic().Coverage.PolicyEffDate, out tmpEffDate))
                response.quoteHeader.PolicyEffectiveDate = tmpEffDate.ToString("MM/dd/yyyy");

            response.quoteHeader.PartnerId = restoredHeader.PartnerID == null ? 0 : Convert.ToInt16(restoredHeader.PartnerID);
            response.quoteHeader.FormCode = restoredHeader.FormNumber == null ? 0 : Convert.ToInt16(restoredHeader.FormNumber);
            response.quoteHeader.QuoteNumber = restoredHeader.QuoteNumber;

            var log = ECommerceServiceLibaryLogger.GetInstance;
            log.LogActivity("Got System Date: " + qHeader.SystemDate);

            response.quoteHeader.Addr1 = this.BusinessLogic().PropertyAddress.Addr1;
            response.quoteHeader.Addr2 = this.BusinessLogic().PropertyAddress.Addr2;
            response.quoteHeader.Zip = this.BusinessLogic().PropertyAddress.PostalCode;
            response.quoteHeader.City = this.BusinessLogic().PropertyAddress.City;
            response.quoteHeader.State = this.BusinessLogic().PropertyAddress.StateProvCd;

            this.BusinessLogic().Header.State = this.BusinessLogic().PropertyAddress.StateProvCd;

            response.quoteHeader.MRPFlag = this.BusinessLogic().Quote.RentersMRP == 1;
            response.quoteHeader.SessionId = this.BusinessLogic().QHeader.SessionId;

            var amf = QuoteServices.GetAMFAccountNumberByPartnerID(this.BusinessLogic().QHeader.PartnerId);

            response.quoteHeader.UnderwritingCompanyNumber =
                DirectWebDAC.GetUWCompany(this.BusinessLogic().QHeader.State, amf, 
                        QuoteServices.GetRatingDate(this.BusinessLogic().Coverage.PolicyEffDate, Convert.ToString(this.BusinessLogic().Quote.IssueDate),this.BusinessLogic().QHeader.State), 
                        this.BusinessLogic().QHeader.FormCode);

            this.BusinessLogic().Header.UnderwritingNumber = response.quoteHeader.UnderwritingCompanyNumber;


            short subProgramType;
            short retroFlag;
            var programType = DirectWebDAC.GetProgramType(
                    QHeader.State,
                    Convert.ToDateTime(this.BusinessLogic().Coverage.PolicyEffDate),
                    out subProgramType,
                    out retroFlag);

            response.quoteHeader.ProgramType = programType;
            this.BusinessLogic().Header.ProgramType = programType;

            response.quoteHeader.SubProgramType = subProgramType;
            this.BusinessLogic().Header.SubProgramType = subProgramType;

            response.quoteHeader.RetroFlag = retroFlag;
            this.BusinessLogic().Header.RetroFlag = retroFlag;


            var ratingVersion = QuoteServices.GetRatingVersion(this.BusinessLogic().Quote, this.BusinessLogic().Header, this.BusinessLogic().Coverage);
            response.quoteHeader.RatingVersion = ratingVersion;
            this.BusinessLogic().Header.RatingVersion = response.quoteHeader.RatingVersion;

            response.quoteHeader.IsApexState = QuoteServices.IsApexState(Convert.ToInt32(this.BusinessLogic().Header.RatingVersion), Convert.ToInt32(this.BusinessLogic().Header.FormNumber));
            QHeader.IsApexState = response.quoteHeader.IsApexState;
            this.BusinessLogic().Header.IsApexState = response.quoteHeader.IsApexState;

            if (!String.IsNullOrEmpty(this.BusinessLogic().Quote.ApexBillingOption))
            {
                QHeader.ApexBillingOption = this.BusinessLogic().Quote.ApexBillingOption;
            }

            var ratingCaseNumber = 0;
            if (!string.IsNullOrEmpty(this.BusinessLogic().Quote.RatingCaseNumber))
            {
                ratingCaseNumber = Convert.ToInt32(this.BusinessLogic().Quote.RatingCaseNumber);
            }
            else if (this.BusinessLogic().Quote.DefaultRatingCaseNumber != null &&
                     this.BusinessLogic().Quote.DefaultRatingCaseNumber != 0)
            {
                ratingCaseNumber = Convert.ToInt16(this.BusinessLogic().Quote.DefaultRatingCaseNumber);
            }
            else
            {
                this.BusinessLogic().Quote.DefaultRatingCaseNumber = 0;
                this.BusinessLogic().Quote.RatingCaseNumber = "0";
            }

            if (ratingCaseNumber > 0)
            {
                response.quoteHeader.RatingTier = DirectWebDAC.GetRatingTier(ratingCaseNumber);
            }


            response.quoteHeader.OriginalQuoteDate = this.BusinessLogic().Quote.OriginalQuotationDate.ToString();
            response.quoteHeader.TotalPremium = this.BusinessLogic().Quote.PolicyPremiumAmt.ToString();
            response.quoteHeader.IsCatZoneZip = DirectWebDAC.IsCatZoneZip(this.QHeader.Zip, this.QHeader.State);
            //ITR#8945
            response.quoteHeader.IsHomeBusinessEndorsementAvailable = ContactCustomerCareProcessor.GetIsHomeBusinessEndorsementAvailableFlag();
            // Yield - story # 612 - ITR # 9745 
            response.quoteHeader.IsChatEnabledState = DirectWebDAC.GetIsBoldChatEnabled(this.QHeader.PartnerId, this.QHeader.FormCode, this.QHeader.State);

            //ITR#8522
            this.BusinessLogic().Header.IsXWindState = QuoteServices.IsXWindState(QHeader.State, QHeader.FormCode,
                                            this.BusinessLogic().Coverage.PolicyEffDate == null ? null : Convert.ToString(this.BusinessLogic().Coverage.PolicyEffDate),
                                            this.BusinessLogic().Quote.InitialQuoteRequestDt == null ? null : Convert.ToString(this.BusinessLogic().Quote.InitialQuoteRequestDt),
                                            AddressBrokerOutputs.GetAddressBrokerOutputs(response.quoteHeader.SessionId)[0].WindPool);


            var isXWindState = this.BusinessLogic().Header.IsXWindState;
            if (isXWindState != null)
                response.quoteHeader.XWindFlag = (bool) isXWindState;

            this.BusinessLogic().Header.Save();

        }



        private void InitializeQuoteHeader()
        {
            string amf = QuoteServices.GetAMFAccountNumberByPartnerID(this.BusinessLogic().QHeader.PartnerId);

            this.BusinessLogic().Header.UnderwritingNumber =
                DirectWebDAC.GetUWCompany(this.BusinessLogic().QHeader.State, amf,
                                                        QuoteServices.GetRatingDate(this.BusinessLogic().Coverage.PolicyEffDate, Convert.ToString(this.BusinessLogic().Quote.IssueDate), this.BusinessLogic().QHeader.State), 
                                                        this.BusinessLogic().QHeader.FormCode);

            this.QHeader.UnderwritingCompanyNumber = (int)this.BusinessLogic().Header.UnderwritingNumber;
            this.BusinessLogic().Quote.UWCompanyNumber = this.BusinessLogic().Header.UnderwritingNumber;


            short subProgramType;
            short retroFlag;

            short programType = DirectWebDAC.GetProgramType(
                    QHeader.State,
                    Convert.ToDateTime(this.BusinessLogic().Coverage.PolicyEffDate),
                    out subProgramType,
                    out retroFlag);

            this.BusinessLogic().Header.ProgramType = programType;
            this.QHeader.ProgramType = programType;

            this.BusinessLogic().Header.SubProgramType = subProgramType;
            this.QHeader.SubProgramType = subProgramType;

            this.BusinessLogic().Header.RetroFlag = retroFlag;
            this.QHeader.ProgramType = programType;
            this.QHeader.SubProgramType = subProgramType;
            this.QHeader.RetroFlag = retroFlag;
            this.QHeader.IsCatZoneZip = DirectWebDAC.IsCatZoneZip(QHeader.Zip, QHeader.State);

            int ratingVersion;
            ratingVersion = QuoteServices.GetRatingVersion(this.BusinessLogic().Quote, this.BusinessLogic().Header, this.BusinessLogic().Coverage);

            this.BusinessLogic().Header.RatingVersion = ratingVersion;
            this.QHeader.RatingVersion = ratingVersion;

            this.QHeader.IsApexState = QuoteServices.IsApexState(Convert.ToInt32(this.BusinessLogic().Header.RatingVersion), Convert.ToInt32(this.BusinessLogic().Header.FormNumber));
            this.BusinessLogic().Header.IsApexState = this.QHeader.IsApexState;

            //ITR#8945
            this.QHeader.IsHomeBusinessEndorsementAvailable = ContactCustomerCareProcessor.GetIsHomeBusinessEndorsementAvailableFlag();

            //ITR#8522
            this.BusinessLogic().Header.IsXWindState = QuoteServices.IsXWindState(QHeader.State, QHeader.FormCode,
                                            this.BusinessLogic().Quote.PolicyEffectiveDate == null ? null : Convert.ToString(this.BusinessLogic().Quote.PolicyEffectiveDate),
                                            this.BusinessLogic().Quote.InitialQuoteRequestDt == null ? null : Convert.ToString(this.BusinessLogic().Quote.InitialQuoteRequestDt),
                                            this.BusinessLogic().PropertyAddressBrokerOutput.WindPool);

            var isXWindState = this.BusinessLogic().Header.IsXWindState;
            if (isXWindState != null)
                this.QHeader.XWindFlag = (bool) isXWindState;

            this.BusinessLogic().Header.Save();
            this.BusinessLogic().Quote.Save();

        }


        private bool ValidateWindHailDeductable()
        {
            bool rtn = true;

            short aTerritory = (this.BusinessLogic().PropertyAddressBrokerOutput.ATerritory == null) ? (short)-1 : (short)this.BusinessLogic().PropertyAddressBrokerOutput.ATerritory;
            DateTime policyEffectiveDate = DateTime.Parse(this.BusinessLogic().Coverage.PolicyEffDate);
            short programType = this.BusinessLogic().QHeader.ProgramType;

            if (Homesite.ECommerce.DAC.DirectWebDAC.GetStateWindHailFlag(this.BusinessLogic().QHeader.State, policyEffectiveDate, aTerritory, programType))
            {
                // NOTE: Removed (qHeader.FormCode != 1)
                if (!(string.IsNullOrEmpty(this.BusinessLogic().Coverage.HurricaneDeductible)) &&
                    (!this.BusinessLogic().Coverage.HurricaneDeductible.Equals("0")) &&
                    !(this.BusinessLogic().Coverage.HurricaneDeductible.IndexOf("%") > 0))
                {
                    int deductible = int.Parse(this.BusinessLogic().Coverage.Deductible.Trim().Replace("$", ""));
                    int hurricaneDeductible = int.Parse(this.BusinessLogic().Coverage.HurricaneDeductible.Trim().Replace("$", ""));
                    if (deductible > hurricaneDeductible)
                    {
                        this.ErrorMessage = "Wind/Hail deductible must not be less than all perils deductible amount.";
                        rtn = false;
                    }

                }
            }

            return rtn;
        }



        private bool _savingQuote;

        public AjaxResponse SaveQuote()
        {
            var response = new AjaxResponse();

            _savingQuote = true;

            var isCoveragePage = this.BusinessLogic().QHeader.Step == "Coverage";

            var savedQuote = false;
            if (!isCoveragePage && qHeader.Step != "Purchase")
            {
                ProcessPage();
                savedQuote = true;
            }
            else
            {
                if (isCoveragePage && !ValidateWindHailDeductable())
                {
                    response.errorMessage = this.ErrorMessage;
                    return response;
                }

                if (isCoveragePage)
                {
                    response = ReCalculate(this.QHeader, this.QData);
                    this.DNQInterface.CallAllDNQ();
                }

                savedQuote = this.ServiceInterface.CallSaveQuoteService();
            }

            if (savedQuote)
            {

                switch (qHeader.Step)
                {
                    case "Coverage":
                        response.NextPanel = AjaxPanel.Coverage;
                        break;
                }

                if (this.ABTestInterface.IsMPQScreenTest())
                {
                    response.message = string.Format("<h1>Quote saved successfully!</h1> " +
                    "<p>Your Quote Number is <b>{0}</b>. " +
                    "Please click on 'View Quote' to download a PDF file that contains all the quote details.</p>",
                    this.qHeader.QuoteNumber);
                }
                else
                {
                    response.message = "Quote saved successfully! Your Quote Number is " + this.qHeader.QuoteNumber +
                                   ".  Please click on 'View Quote' link to download a PDF file that contains all the quote details.";
                }
            }
            else
            {
                response.message = "Quote save failed! Please try again.";
            }

            _savingQuote = false;
            return response;

        }

        public InstallmentDetails GetInstallmentDetails()
        {
            return this.PurchaseProcessor.GetInstallmentData();

        }

        public string GetScheduleDetails(QuoteHeader QHeader)
        {
            return this.PurchaseProcessor.GetInstallmentSchedule(QHeader);

        }

        public BillingOptions GetBillingOptions()
        {
            this.Log("GetBillingOptions - Start... Inspection Result value = " + this.BusinessLogic().Quote.InspectionResult.ToString());

            BusinessLogicPurchaseProcessor Purchase = new BusinessLogicPurchaseProcessor();
            this.Accept<BusinessLogicPurchaseProcessor>(Purchase);

            BusinessLogicSessionDataBinder data = new BusinessLogicSessionDataBinder();
            this.Accept<BusinessLogicSessionDataBinder>(data);

            data.RefreshDataObjects();

            this.Log("GetBillingOptions - DataObjects refreshed... Inspection Result value = " + this.BusinessLogic().Quote.InspectionResult.ToString());

            int propertyType = 0;

            Structure structure = Structure.GetStructure(QHeader.SessionId);

            if (!String.IsNullOrEmpty(structure.PropertyType))
            {
                propertyType = Convert.ToInt32(structure.PropertyType);
            }
            //QC#285 - getting RatingDate
            DateTime? policyEffectiveDt = Convert.ToDateTime(data.Coverage.PolicyEffDate);
            LookupProvider lookupProvider = new LookupProvider();
            DateTime? ratingDate = lookupProvider.GetRatingDate(policyEffectiveDt,
                                                      data.Quote.InitialQuoteRequestDt,
                                                      QHeader.State);

            Homesite.IQuote.Data.ISODataProvider.hssp_GetInstallmentDataDataTable installmentDataTable =
                QuoteServices.GetInstallmentDataTable(data.Quote.CompanysQuoteNumber, QHeader.State, Convert.ToDecimal(data.Quote.PolicyPremiumAmt),
                Convert.ToDateTime(ratingDate), propertyType);

            BillingOptions billingOptions = new BillingOptions();

            foreach (Homesite.IQuote.Data.ISODataProvider.hssp_GetInstallmentDataRow row in installmentDataTable.Rows)
            {
                if (row.NumOfInstallments == 0)
                {
                    billingOptions.FullPremium = String.Format("{0:$###,###,##0.00}", row.NewDownPayment);
                }
                else if (row.NumOfInstallments == 1)
                {
                    billingOptions.DownPayment_2Pay = String.Format("{0:$###,###,##0.00}", row.NewDownPayment);
                    billingOptions.PaymentAmt_2Pay = String.Format("{0:$###,###,##0.00}", row.Installment);
                }
                else if (row.NumOfInstallments == 3)
                {
                    billingOptions.DownPayment_4Pay = String.Format("{0:$###,###,##0.00}", row.NewDownPayment);
                    billingOptions.PaymentAmt_4Pay = String.Format("{0:$###,###,##0.00}", row.Installment);
                }
                else if (row.NumOfInstallments == 9)
                {
                    billingOptions.DownPayment_10Pay = String.Format("{0:$###,###,##0.00}", row.NewDownPayment);
                    billingOptions.PaymentAmt_10Pay = String.Format("{0:$###,###,##0.00}", row.Installment);
                }
                else if (row.NumOfInstallments == 11)
                {
                    billingOptions.DownPayment_12Pay = String.Format("{0:$###,###,##0.00}", row.NewDownPayment);
                    billingOptions.PaymentAmt_12Pay = String.Format("{0:$###,###,##0.00}", row.Installment);
                }
            }

            this.Log("GetBillingOptions - end... Inspection Result value = " + this.BusinessLogic().Quote.InspectionResult.ToString());

            return billingOptions;

        }


        #endregion


        /// <summary>
        /// all form codes use this.
        /// </summary>
        private void ProcessWelcome()
        {
            DataBinder.SaveMarketingInfo();

            this.BusinessLogic().Header.Last_PageVisit_ID = 5;
            this.BusinessLogic().Header.Save();


            Log("Validating Zip.");

            ServiceInterface.CallValidateZipCodeService();

            if (string.IsNullOrEmpty(this.BusinessLogic().PropertyAddress.StateProvCd) &&
                string.IsNullOrEmpty(this.BusinessLogic().PropertyAddress.City))
            {
                InfoMessage = "Couldn't Process this Zip.";
                return;
            }

            if (!this.ServiceInterface.CallCheckStateElegibility())
            {
                this.IsIneligible = true;
                this.BusinessLogic().Quote.DNQFlag = true;
                this.BusinessLogic().Quote.DNQReasons = "InEligible based on state";
                this.BusinessLogic().Quote.Save();
                return;
            }
            this.BusinessLogic().Quote.RetrieveFlag = true;

            this.BusinessLogic().QHeader.PolicyEffectiveDate = DateTime.Today.ToString();
            this.BusinessLogic().QHeader.IsStateEligible =
                Homesite.ECommerce.DAC.DirectWebDAC.GetWalmartCustomerEligilbeState(this.BusinessLogic().QHeader.State, this.BusinessLogic().QHeader.PartnerId, DateTime.Parse(this.BusinessLogic().QHeader.PolicyEffectiveDate), this.BusinessLogic().QHeader.FormCode);
            this.DataBinder.SaveDataObjects();
        }

        private void ProcessQuoteStart()
        {
            // hate you, last_pagevisit_id.
            if (this.BusinessLogic().Header.Last_PageVisit_ID.HasValue == false || this.BusinessLogic().Header.Last_PageVisit_ID != 6)
            {
                this.BusinessLogic().Header.Last_PageVisit_ID = 5;
                this.BusinessLogic().Header.Save();
            }

            // Check for changed address before saving new data.
            IsAddressVerified = (IsAddressVerified && !(UserChangedAddress()));

            DataBinder.BindSessionToQuoteStartData();

            if (!IsAddressVerified || (IsAddressVerified && UserChangedAddress()))
            {
                DataBinder.ClearPropertyAddressPostalCodeExt();

                ServiceInterface.CallAddressBrokerService();
                var isExactMatch = ServiceInterface.IsExactMatch(
                    this.BusinessLogic().PropertyAddressBrokerOutput.MatchCode,
                    this.BusinessLogic().PropertyAddressBrokerOutput.LocationQualityCode);

                IsAddressVerified = isExactMatch;

                if (!IsAddressVerified)
                    return;
            }

            this.DNQInterface.CallAddressDNQs();

            if (!this.IsDnq)
                this.DNQInterface.CallCallCenterValidateState();


            if (IsDnq)
            {
                this.IsDnq = true;
                this.IsIneligible = !this.IsDnq;
            }
            else if (IsIneligible)
            {
                this.IsDnq = false;
                this.IsIneligible = !this.IsDnq;
            }
            else
            {
                this.DataBinder.SaveDefaultAddress();

                this.QHeader.QualityGroup = QuoteServices.GetQualityGroup(qHeader, this.BusinessLogic().PropertyAddress.PostalCode);

                this.ServiceInterface.CallProtectionService();

                if (QHeader.FormCode == 3)
                {
                    this.ServiceInterface.CallTaxRetrivalService();
                }

                this.ServiceInterface.CallSaveQuoteServiceAsync();
            }
        }

           
        private void ProcessQuoteRenters()
        {
            DataBinder.QuoteDataToSessionDatabase();

            if (!SkipGamingCheck)
            {
                Log("Gaming Check...");
                ServiceInterface.CallCheckGaming();
            }

            if (IsIneligible) return;
            if (IsDnq) return;

            Log("Getting a rate.");
            ServiceInterface.CallRatingService();

            if ((this.BusinessLogic().Quote.PolicyPremiumAmt == null || this.BusinessLogic().Quote.PolicyPremiumAmt == 0))
            {
                this.Log("Zero premium DNQ");

                this.IsIneligible = true;
                this.NextAjaxPanel = AjaxPanel.InEligible;
                this.BusinessLogic().Quote.DNQReasons = "Policy premium amount is $0";
                this.BusinessLogic().Quote.Save();
                return;
            }

            // ITR#8522 LA-XWind
            if (QHeader != null)
            {
                var isXWindState = this.BusinessLogic().Header.IsXWindState;
                if (isXWindState != null)
                    QHeader.XWindFlag = (bool)isXWindState;
            }

            // save the default premium
            this.BusinessLogic().Quote.DefaultPremium = this.BusinessLogic().Quote.DefaultPremium ?? (int)this.BusinessLogic().Quote.PolicyPremiumAmt;
            this.BusinessLogic().Quote.DefaultRatingCaseNumber = this.BusinessLogic().Quote.DefaultRatingCaseNumber ?? int.Parse(this.BusinessLogic().Quote.RatingCaseNumber);

           
            this.Log("Saving the quote to horizon");
            this.ServiceInterface.CallSaveQuoteService();
        }

        private void ProcessQuoteHomeowners()
        {
            int previousRatinvVersion = this.BusinessLogic().QHeader.RatingVersion;
            var validMandS = true;

            DataBinder.QuoteDataToSessionDatabase();

            ServiceInterface.CallDefaultService();

            DataBinder.PopulateAvailableCredit();

            ServiceInterface.CallDangerousDogCoverageDefaults();

            if (!SkipGamingCheck)
            {
                ServiceInterface.CallCheckGaming();
            }

            if (IsIneligible) return;

            DNQInterface.CallPreCreditDNQ();

            if (IsDnq) return;
            bool isAutoDiscountAvailable = false;
            if (!IsDnq && !IsIneligible)
            {
                ServiceInterface.CallValidateBuildingCostRules();

                if (ServiceInterface.HasErrorMessage)
                {
                    ErrorMessage = ServiceInterface.ErrorMessage;
                    return;
                }

                validMandS = string.IsNullOrEmpty(QHeader.MandSerror);
                if (validMandS) // to avoid excessive service calls
                {
                    //ITR#9651 Good Driver -- Start
                    isAutoDiscountAvailable = DirectWebDAC.IsGoodDriverDiscountAvailable(QHeader.RatingVersion);

                    if (isAutoDiscountAvailable)
                    {
                        ServiceInterface.CallAplusAutoServiceAsync();
                    }
                    //ITR#9651 Good Driver -- End
                    ServiceInterface.CallCreditServiceAsynch();                    
                    ServiceInterface.CallRebuildCostService();
                    ServiceInterface.CallHomeOwnersCoverageDetailsDefaultService();

                    DNQInterface.CallPostRebuildCostDNQ();
                }
            }            

            if (!IsDnq && !IsIneligible && validMandS)
            {
                ServiceInterface.WaitForCreditService();
                ServiceInterface.ProcessCreditResults();
                //ITR#9651 Good Driver
                if(isAutoDiscountAvailable)
                {
                    ServiceInterface.WaitForAplusAutoService();
                    ServiceInterface.ProcessAutoClaimsResult();
                }
                if (qHeader.FormCode != 4)
                {
                    DNQInterface.CallPostCreditDNQ();
                }
            }
           //ITR#8945 - Process Home Business Logic
            if (!IsDnq && !IsIneligible && validMandS)
            {

                if (qHeader.FormCode == 3)
                {
                    ContactCustomerCareProcessor.ProcessHomeBusinessEndorsementLogic();
                }
            }

            if (!IsDnq && !IsIneligible && !IsContactCustomerCare && validMandS)
            {
                decimal? premiumAmt = this.BusinessLogic().Quote.PolicyPremiumAmt;

                //if (this.BusinessLogic().QHeader.IsMultiRate)
                //{
                //    UpdateWindHailOrHurricaneValues(this.BusinessLogic().Coverage.Deductible.Trim());
                //}
                                
                ServiceInterface.CallRatingService();

                LogConsumerEvents(premiumAmt);

                if ((this.BusinessLogic().Quote.PolicyPremiumAmt == null || this.BusinessLogic().Quote.PolicyPremiumAmt == 0))
                {
                    IsIneligible = true;
                    NextAjaxPanel = AjaxPanel.InEligible;
                    this.BusinessLogic().Quote.DNQReasons = "Policy premium amount is $0";
                    this.BusinessLogic().Quote.Save();
                    return;
                }

                this.BusinessLogic().Quote.DefaultPremium = this.BusinessLogic().Quote.DefaultPremium ??
                                                     (int)this.BusinessLogic().Quote.PolicyPremiumAmt;
                this.BusinessLogic().Quote.DefaultRatingCaseNumber = this.BusinessLogic().Quote.DefaultRatingCaseNumber ??
                                                              int.Parse(this.BusinessLogic().Quote.RatingCaseNumber);

                if (MultiRateProcessor.IsEnabled() && displaySSNModal == false)
                {
                    this.Log("Calling ProcessMultiRateForm3..");
                    MultiRateProcessor.ProcessMultiRateForm3();
                }
                
                // We can't save Asynch in the fee states b/c we need horizon to calc state fees.
                if (QuoteServices.IsFeeState(qHeader.State))
                {
                    ServiceInterface.CallSaveQuoteService();
                }
                else
                {
                    ServiceInterface.CallSaveQuoteServiceAsync();
                }



                // ITR#8522 LA-XWind
                if (QHeader != null)
                {
                    var isXWindState = this.BusinessLogic().Header.IsXWindState;
                    if (isXWindState != null)
                        QHeader.XWindFlag = (bool)isXWindState;
                }
            }

            // **********************************************************************************************************************************************************************************
            // ITR # 6815 : Added below logic to have up-to-date coverage values(means default values) in the session database.While testing ITR#6815, We got logical issue when printing the 
            // quote report before saving and recalculate coverage data. 
            // ***********************************************************************START******************************************************************************************************
            //this.DataBinder.BindCoverageData();
            // Gets the dedutible options from the look up provider for this rating version.
            //ControlDataSource cds = new ControlDataSource(DirectWebDAC.GetDeductible(this.DataBinder.QHeader.RatingVersion, this.DataBinder.QHeader.FormCode,
            //                           this.DataBinder.QHeader.ProgramType, this.DataBinder.QHeader.State), "DeductibleOption", "DeductibleOption");
            //if (!string.IsNullOrEmpty(this.DataBinder.Coverage.Deductible) && !cds.Items.Keys.Contains(this.DataBinder.Coverage.Deductible))
            //{
            // The previouse selected deductible is no longer offered in the new rating version.
            //    this.DataBinder.Coverage.Deductible = cds.Items.First().Key;
            //    this.DataBinder.Coverage.Save();
            //}
            // **************************************************************************END*****************************************************************************************************
        }

        private void UpdateWindHailOrHurricaneValues(string selectedDeductible)
        {
            if (MultiRateProcessor.IsEnabled()) return;

            this.Log("Calling UpdateWindHailOrHurricaneValues with deductible " +QData.cvgDeductible);
            
            ControlDataSource windHailOptions = GetWindHailDataSource(this.QHeader, this.BusinessLogic().Coverage,
                                                                    AddressBrokerOutputs.GetAddressBrokerOutputs(this.QHeader.SessionId)[0]);

            if (windHailOptions != null && windHailOptions.Items.Count > 0)
            {
                string windHailSelection = string.Empty;
                string hurricaneSelection = string.Empty;
                switch (this.QHeader.FormCode)
                {
                    case 3:
                        windHailSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH03(windHailOptions,selectedDeductible,this.BusinessLogic().Coverage.CovAAmount.ToString(),this.BusinessLogic().Coverage.WindstormDeductible).Trim();
                        hurricaneSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH03(windHailOptions,selectedDeductible,this.BusinessLogic().Coverage.CovAAmount.ToString(),this.BusinessLogic().Coverage.HurricaneDeductible).Trim();
                        break;
                    case 4:
                        windHailSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH04(windHailOptions,selectedDeductible,this.BusinessLogic().Coverage.WindstormDeductible).Trim();
                        hurricaneSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH04(windHailOptions,selectedDeductible,this.BusinessLogic().Coverage.HurricaneDeductible).Trim();
                        break;
                }

                if (windHailSelection.ToUpper().Trim().Contains("NO COVERAGE"))
                {
                    bool isUpdated = false;
                    if (this.BusinessLogic().Coverage.HurricaneDeductible != null &&
                        this.BusinessLogic().Coverage.HurricaneDeductible != hurricaneSelection)
                    {
                        this.BusinessLogic().Coverage.HurricaneDeductible = hurricaneSelection;
                        isUpdated = true;
                    }
                    if (this.BusinessLogic().Coverage.WindstormDeductible != null &&
                        this.BusinessLogic().Coverage.WindstormDeductible != windHailSelection)
                    {
                        this.BusinessLogic().Coverage.WindstormDeductible = windHailSelection;
                        isUpdated = true;
                    }
                    if (isUpdated)
                    {
                        this.Log("Saving WindstormDeductible..");
                        this.BusinessLogic().Coverage.Save();
                    }
                }
            }
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="selectedDeductible"></param>
        /// <param name="windHailSelection"></param>
        /// <param name="hurricaneSelection"></param>
        /// <param name="isUpdated"></param>
        private void GeteWindHailOrHurricaneValues(string selectedDeductible,out string windHailSelection, out string hurricaneSelection,out bool isUpdated)
        {
            windHailSelection = string.Empty;
            hurricaneSelection = string.Empty;
            isUpdated = false;

            this.Log("Calling GeteWindHailOrHurricaneValues..");

            ControlDataSource windHailOptions = GetWindHailDataSource(this.QHeader,
                                                                          this.BusinessLogic().Coverage,
                                                                          AddressBrokerOutputs.
                                                                              GetAddressBrokerOutputs(
                                                                                  header.SessionID)[0]);
            if (windHailOptions != null && windHailOptions.Items.Count > 0)
            {
                switch (this.QHeader.FormCode)
                {
                    case 3:
                        windHailSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH03(windHailOptions,selectedDeductible,this.BusinessLogic().Coverage.CovAAmount.ToString(),this.BusinessLogic().Coverage.WindstormDeductible).Trim();
                        hurricaneSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH03(windHailOptions,selectedDeductible,this.BusinessLogic().Coverage.CovAAmount.ToString(),this.BusinessLogic().Coverage.HurricaneDeductible).Trim();
                        break;
                    case 4:
                        windHailSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH04(windHailOptions,selectedDeductible,this.BusinessLogic().Coverage.WindstormDeductible).Trim();
                        hurricaneSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH04(windHailOptions,selectedDeductible,this.BusinessLogic().Coverage.HurricaneDeductible).Trim();
                        break;
                }

               
                if (this.BusinessLogic().Coverage.HurricaneDeductible != null && this.BusinessLogic().Coverage.HurricaneDeductible != hurricaneSelection)
                {
                   isUpdated = true;
                }
                if (this.BusinessLogic().Coverage.WindstormDeductible != null && this.BusinessLogic().Coverage.WindstormDeductible != windHailSelection)
                {
                    isUpdated = true;
                }
            }
        }

        //private string GetHighestMultiRateDeductible()
        //{
        //     string maximumString = string.Empty;
        //    switch (this.QHeader.FormCode)
        //    {
        //        case 4:
        //            {
        //                int maximum = 0;
                       
        //                var options =
        //                    (new UIServices()).GetMultiRateOptions(this.QHeader.FormCode, this.QHeader.State) as
        //                    MultiRateOptionsForm4;
        //                    for (int i = 0; (options != null) && i < 3; i++)
        //                    {
        //                        string currentVal = options.Deductible[i].Replace("$", string.Empty).Trim();
        //                        if (currentVal.Length > 0 && maximum < Convert.ToInt32(currentVal))
        //                        {
        //                            maximum = Convert.ToInt32(currentVal);
        //                            maximumString = options.Deductible[i];
        //                        }
        //                    }
                        
        //            }
        //            break;
        //        case 3:
        //            {

        //                int maximum = 0;
                       
        //                var options =
        //                    (new UIServices()).GetMultiRateOptions(this.QHeader.FormCode, this.QHeader.State) as
        //                    MultiRateOptionsForm3;
        //                    for (int i = 0; (options != null) && i < 3; i++)
        //                    {
        //                        string currentVal = options.Deductible[i].Replace("$", string.Empty).Trim();
        //                        if (currentVal.Length > 0 && maximum < Convert.ToInt32(currentVal))
        //                        {
        //                            maximum = Convert.ToInt32(currentVal);
        //                            maximumString = options.Deductible[i];
        //                        }
        //                    }

        //            }
        //            break;
        //    }

        //    return maximumString;
        //}

        private void ProccessCoverages()
        {
            int inspectionResult = -2;

            if (qHeader.FormCode == 3)
            {
                this.Log("Calling APlus..");
                this.ServiceInterface.CallAPlusService(out this.displayAPlusModal);
                this.Log("Calling GetInspectionCode..");

                // Calling and Logging Inspection Model
                inspectionResult = ServiceInterface.GetInspectionCode();
                this.Log("GetInspectionCode result : " + inspectionResult.ToString());
            }
            else if (qHeader.FormCode == 6)
            {
                this.Log("Calling APlus..");

                this.ServiceInterface.CallAPlusService(out this.displayAPlusModal);
            }

            if (!String.IsNullOrEmpty(this.ServiceInterface.ErrorMessage))
            {
                this.ErrorMessage = this.ServiceInterface.ErrorMessage;
            }
            else
            {
                this.Log("ProccessCoverages - Saving Quote..");
                if (QuoteServices.IsFeeState(qHeader.State))
                {
                    this.ServiceInterface.CallSaveQuoteService();
                }
                else
                {
                    this.ServiceInterface.CallSaveQuoteServiceAsync();
                }
                this.Log("ProccessCoverages - Quote Saved... Inspection Result value = " + this.BusinessLogic().Quote.InspectionResult.ToString());
            }

            // Save the tracking id to quote submaster on complete quotes
            DirectWebDAC.InsertTrackingId(this.BusinessLogic().QHeader.QuoteNumber, this.BusinessLogic().QHeader.TrackingId);

            // ITR#7684
           // this.BusinessLogic().QHeader.XWindFlag = (!string.IsNullOrEmpty(this.BusinessLogic().PropertyAddressBrokerOutput.WindPool) && this.BusinessLogic().PropertyAddressBrokerOutput.WindPool == "IN");

            var isXWindState = this.BusinessLogic().Header.IsXWindState;
            if (isXWindState != null)
                this.BusinessLogic().QHeader.XWindFlag = (bool) isXWindState;
            this.BusinessLogic().QHeader.CtrFlag = this.BusinessLogic().Quote.CTRFlag ?? string.Empty;

            if (ProcessWalmartFlow())
            {
                SetWalmartSpecificHeaderFields();
            }
            // Below logic will be called if user does not enter discount.
            if (this.BusinessLogic().QHeader.PartnerId.Equals(2319))
            {
                //we need to strip off the discount here 
                if (string.IsNullOrEmpty(this.BusinessLogic().QData.cvgPartnerDiscount) && this.BusinessLogic().QHeader.OLSStatus != OlsStatusEmailValidated)
                {
                    this.BusinessLogic().Quote.AutoPolicyNumber = string.Empty;
                    this.BusinessLogic().Quote.PartnerAutoPolicy = 0;
                    this.BusinessLogic().Quote.Save();
                    this.ServiceInterface.CallRatingService();
                }
            }
            if (this.BusinessLogic().QHeader.OLSStatus == OlsStatusEmailValidated)
            {
                ValidationService.ValidationServiceClient validationClient = null;
                try
                {
                    validationClient = new ValidationServiceClient();
                    DataTable table = DirectWebDAC.GetOLSEnrollmentValues(QHeader.QuoteNumber);
                    ValidationServiceMessage validationService = validationClient.ValidateResponse(table.Rows[0]["Token"].ToString(), table.Rows[0]["EMailVerificationCode"].ToString());
                    if (!validationService.Result)
                    {
                        //Call failed, try again or set OLSStatus to a failure state
                    }

                }
                catch (FaultException e)
                {
                    // OLSStatus to error for customer ~ -5?

                }
                finally
                {
                    if (validationClient != null)
                    {
                        validationClient.Close();
                    }
                }
            }

            // For Apex we HAVE to re rate after saving down the selected billing option.
            if (qHeader.IsApexState)
            {
                this.BusinessLogic().Quote.ApexBillingOption = qHeader.ApexBillingOption;
                this.BusinessLogic().Quote.Save();
                this.ServiceInterface.CallRatingService();
            }

            this.BusinessLogic().Coverage.Save();
        }

        private void ProcessAPlus()
        {
            this.displayAPlusModal = false;

            if (this.BusinessLogic().Quote.APLUSStatus != null)
            {
                int APLUSStatus = Convert.ToInt16(this.BusinessLogic().Quote.APLUSStatus);
                decimal? oldPremium = this.BusinessLogic().Quote.PolicyPremiumAmt ?? 0;
                int oldClaims = this.BusinessLogic().Claims.Count;
                if (APLUSStatus == 0 || APLUSStatus == 2 || APLUSStatus == 3)
                {
                    this.DNQInterface.CallAllDNQ();

                    if (!IsDnq)
                    {
                        // set aplus status to accepted
                        this.BusinessLogic().Quote.APLUSStatus = 2;
                        this.DataBinder.SaveDataObjects();

                        this.DataBinder.PreviousLoss_ToQuoteData();

                        this.ServiceInterface.CallRatingService();

                        this.ServiceInterface.CallSaveQuoteService();

                        if (oldPremium != this.BusinessLogic().Quote.PolicyPremiumAmt && this.BusinessLogic().Claims.Count > 0)
                        {
                            LogRateChangeEvent(5);
                        }
                        else if (oldPremium != this.BusinessLogic().Quote.PolicyPremiumAmt && this.BusinessLogic().Claims.Count == 0)
                        {
                            LogRateChangeEvent(6);
                        }

                    }
                    else
                    {
                        IsDnq = true;
                    }

                }
            }
        }

        private void ProcessForm2Data()
        {
            this.BusinessLogic().QHeader.FormCode = 2;
            this.BusinessLogic().QHeader.H02MailApp = QData.mailApp == null ? false : Convert.ToBoolean(QData.mailApp);
            this.IsDnq = true;


            this.BusinessLogic().Header.FormNumber = 2;
            this.BusinessLogic().Quote.CustomerFormSelect = 2;
            this.BusinessLogic().Quote.FormNumber = "2";
            this.BusinessLogic().Quote.CompleteQuoteFlag = -1;

            this.DataBinder.SaveDataObjects();

            this.ServiceInterface.CallSaveQuoteService();

            if (!qData.mailApp.HasValue)
                qData.mailApp = false;

            //Set Print records
            Homesite.IQuote.LookupBroker.LookupProvider LookupProvider = new Homesite.IQuote.LookupBroker.LookupProvider();
            Homesite.IQuote.Data.LookupDataProvider.PartnerInfoRow partnerInfo = LookupProvider.GetPartnerInfoByPartnerID(qHeader.PartnerId);

            int accountNumber = (int)partnerInfo.AMFAccountNumber;
            int levelOneNo = (int)partnerInfo.Level1Number;
            int levelTwoNo = (int)partnerInfo.Level2Number;

            Homesite.IQuote.Data.ISOPrintDataProviderTableAdapters.QueriesTableAdapter iSOPrint = new Homesite.IQuote.Data.ISOPrintDataProviderTableAdapters.QueriesTableAdapter();

            iSOPrint.hssp_print_batch_update_HO2_Application_mailing_record(
                this.BusinessLogic().Quote.DateTimeCreated,
                accountNumber,
                levelOneNo,
                levelTwoNo,
                this.BusinessLogic().Quote.CompanysQuoteNumber,
                this.BusinessLogic().Header.UnderwritingNumber.ToString(),
                this.BusinessLogic().Header.State,
                this.BusinessLogic().PrimaryPerson.LastName,
                this.BusinessLogic().PrimaryPerson.FirstName,
                (qData.mailApp == true) ? this.BusinessLogic().MailingAddress.Addr1 : string.Empty,
                (qData.mailApp == true) ? this.BusinessLogic().MailingAddress.Addr2 : string.Empty,
                (qData.mailApp == true) ? this.BusinessLogic().MailingAddress.City : string.Empty,
                (qData.mailApp == true) ? this.BusinessLogic().MailingAddress.StateProvCd : string.Empty,
                (qData.mailApp == true) ? this.BusinessLogic().MailingAddress.PostalCode : string.Empty,
                this.BusinessLogic().PropertyAddress.Addr1,
                this.BusinessLogic().PropertyAddress.Addr2,
                this.BusinessLogic().PropertyAddress.City,
                this.BusinessLogic().PropertyAddress.StateProvCd,
                this.BusinessLogic().PropertyAddress.PostalCode,
                this.BusinessLogic().PrimaryPerson.DateOfBirth,
                this.BusinessLogic().Structure.ValueOfHome.ToString()
                );
        }



        private bool SkipGamingCheck;
        /// <summary>
        /// FOR UNIT TESTS!
        /// </summary>
        public void DisableGaming()
        {
            SkipGamingCheck = true;
        }


        private void Log(string message)
        {

            string logmsg = "Consumer Web: " + message;

#if DEBUG
            System.Diagnostics.Debug.WriteLine(DateTime.Now + " " + logmsg);
#endif

            Homesite.Diagnostics.ECommerce.ECommerceServiceLibaryLogger log = Homesite.Diagnostics.ECommerce.ECommerceServiceLibaryLogger.GetInstance;
            log.Trace(-6, logmsg, qHeader.SessionId);
        }


        #region IBusinessLogicVisitable Members




        bool IBusinessLogicVisitable.IsDnq
        {
            get { return IsDnq; }
            set { IsDnq = value; }
        }
        bool IBusinessLogicVisitable.IsIneligible
        {
            get { return IsIneligible; }
            set { IsIneligible = value; }
        }

        bool IBusinessLogicVisitable.IsMpq
        {
            get { return IsMpq; }
        }

        bool IBusinessLogicVisitable.IsContactCustomerCare
        {
            get
            {
                return IsContactCustomerCare;
            }
            set
            {
                IsContactCustomerCare = value;
            }
        }
        ABTestGroupCollection IBusinessLogicVisitable.ABTestGroups
        {
            get
            {
                if (abTestGroups == null)
                    abTestGroups = ABTestGroupCollection.GetABTestGroups(qHeader.SessionId);

                return abTestGroups;
            }
            set
            {
                abTestGroups = value;
            }
        }

        AddressBrokerOutputs IBusinessLogicVisitable.AddressBrokerOutputs
        {
            get
            {
                if (addressBrokerOutputs == null)
                    addressBrokerOutputs = AddressBrokerOutputs.GetAddressBrokerOutputs(qHeader.SessionId);

                return addressBrokerOutputs;
            }
            set
            {
                propertyAddressBrokerOutput = null;
                addressBrokerOutputs = value;
            }
        }

        AddressBrokerOutput IBusinessLogicVisitable.PropertyAddressBrokerOutput
        {
            get
            {
                if (this.BusinessLogic().Addresses.Count != 0 && propertyAddressBrokerOutput == null)
                    propertyAddressBrokerOutput = this.BusinessLogic().AddressBrokerOutputs.Where(addressBrokerOutput => addressBrokerOutput.AddressType == "PROPERTY").SingleOrDefault();

                if (propertyAddressBrokerOutput == null && qHeader.SessionId != 0)
                {
                    propertyAddressBrokerOutput = new AddressBrokerOutput();
                    propertyAddressBrokerOutput.SessionId = qHeader.SessionId;
                    propertyAddressBrokerOutput.AddressType = "PROPERTY";
                }

                return propertyAddressBrokerOutput;

            }
            set
            {
                propertyAddressBrokerOutput = null;
            }
        }

        People IBusinessLogicVisitable.People
        {
            get
            {
                if (people == null)
                    people = People.GetPeople(qHeader.SessionId);

                return people;
            }
            set
            {
                primaryPerson = null;
                secondaryPerson = null;
                people = value;
            }
        }

        Structure IBusinessLogicVisitable.Structure
        {
            get
            {
                if (structure == null)
                {
                    try
                    {
                        structure = Structure.GetStructure(qHeader.SessionId);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        structure = new Structure();
                        structure.SessionId = QHeader.SessionId;
                    }
                }

                return structure;
            }
            set
            {
                structure = value;
            }
        }

        Address IBusinessLogicVisitable.MailingAddress
        {
            get
            {

                if (this.BusinessLogic().Addresses.Count != 0 && mailingAddress == null)
                    mailingAddress = this.BusinessLogic().Addresses.Where(address => address.AddressType == "Mailing").SingleOrDefault();

                if (mailingAddress == null && qHeader.SessionId != 0)
                {
                    mailingAddress = new Address();
                    mailingAddress.SessionId = qHeader.SessionId;
                    mailingAddress.AddressType = "Mailing";
                }

                return mailingAddress;
            }
            set
            {
                mailingAddress = value;
            }
        }

        Address IBusinessLogicVisitable.PriorAddress
        {
            get
            {

                if (this.BusinessLogic().Addresses.Count != 0 && priorAddress == null)
                    priorAddress = this.BusinessLogic().Addresses.Where(address => address.AddressType == "PRIOR").SingleOrDefault();

                if (priorAddress == null && qHeader.SessionId != 0)
                {
                    priorAddress = new Address();
                    priorAddress.SessionId = qHeader.SessionId;
                    priorAddress.AddressType = "PRIOR";
                }

                return priorAddress;
            }
            set
            {
                priorAddress = value;
            }
        }

        Address IBusinessLogicVisitable.UserMailingAddress
        {
            get
            {

                if (this.BusinessLogic().Addresses.Count != 0 && userMailingAddress == null)
                    userMailingAddress = this.BusinessLogic().Addresses.Where(address => address.AddressType == "USERMAILING").SingleOrDefault();

                if (userMailingAddress == null && qHeader.SessionId != 0)
                {
                    userMailingAddress = new Address();
                    userMailingAddress.SessionId = qHeader.SessionId;
                    userMailingAddress.AddressType = "USERMAILING";
                }

                return userMailingAddress;
            }
            set
            {
                userMailingAddress = value;
            }
        }

        Address IBusinessLogicVisitable.UserPropertyAddress
        {
            get
            {

                if (this.BusinessLogic().Addresses.Count != 0 && userPropertyAddress == null)
                    userPropertyAddress = this.BusinessLogic().Addresses.Where(address => address.AddressType == "USERPROPERTY").SingleOrDefault();

                if (userPropertyAddress == null && qHeader.SessionId != 0)
                {
                    userPropertyAddress = new Address();
                    userPropertyAddress.SessionId = qHeader.SessionId;
                    userPropertyAddress.AddressType = "USERPROPERTY";
                }

                return userPropertyAddress;
            }
            set
            {
                userPropertyAddress = value;
            }
        }

        Person IBusinessLogicVisitable.PrimaryPerson
        {
            get
            {
                if (this.BusinessLogic().People.Count != 0 && primaryPerson == null)
                    primaryPerson = this.BusinessLogic().People.Where(person => person.CustType == "PRIMARY").First();

                if (primaryPerson == null && qHeader.SessionId != 0)
                {
                    primaryPerson = new Person();
                    this.primaryPerson.BeginEdit();
                    this.primaryPerson.SessionId = qHeader.SessionId;
                    this.primaryPerson.CustType = "PRIMARY";
                    this.primaryPerson.ApplyEdit();
                }

                return primaryPerson;
            }
            set
            {
                //throw new NotImplementedException("Set People Collection Property");
                primaryPerson = value;
            }
        }

        AdditionalCoverage IBusinessLogicVisitable.AdditionalCoverage
        {
            get
            {
                if (this.additionalCoverage == null && qHeader.SessionId != 0)
                {
                    try
                    {
                        additionalCoverage = AdditionalCoverage.GetAdditionalCoverage(qHeader.SessionId);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        additionalCoverage = new AdditionalCoverage();
                        additionalCoverage.SessionId = qHeader.SessionId;
                    }
                }

                return additionalCoverage;
            }
            set
            {
                additionalCoverage = value;
            }
        }

        Person IBusinessLogicVisitable.SecondaryPerson
        {
            get
            {
                if (this.BusinessLogic().People.Count != 0 && secondaryPerson == null)
                    secondaryPerson = this.BusinessLogic().People.Where(person => person.CustType == "SECONDARY").SingleOrDefault();

                if (secondaryPerson == null && qHeader.SessionId != 0)
                {
                    this.secondaryPerson = new Person();
                    this.secondaryPerson.SessionId = qHeader.SessionId;
                    this.secondaryPerson.CustType = "SECONDARY";
                }

                return secondaryPerson;
            }
            set
            {
                secondaryPerson = value;
            }
        }

        Addresses IBusinessLogicVisitable.Addresses
        {
            get
            {
                if (addresses == null)
                    addresses = Addresses.GetAddresses(qHeader.SessionId);

                return addresses;
            }
            set
            {
                mailingAddress = null;
                priorAddress = null;
                propertyAddress = null;
                userMailingAddress = null;
                userPropertyAddress = null;
                addresses = value;
            }
        }

        Address IBusinessLogicVisitable.PropertyAddress
        {
            get
            {

                if (this.BusinessLogic().Addresses.Count != 0 && propertyAddress == null)
                    propertyAddress = this.BusinessLogic().Addresses.Where(address => address.AddressType == "PROPERTY").SingleOrDefault();

                if (propertyAddress == null && qHeader.SessionId != 0)
                {
                    propertyAddress = new Address();
                    propertyAddress.AddressType = "PROPERTY";
                    propertyAddress.SessionId = QHeader.SessionId;
                }

                return propertyAddress;
            }
            set
            {
                propertyAddress = value;
            }
        }

        Coverage IBusinessLogicVisitable.Coverage
        {
            get
            {
                if (coverage == null)
                {
                    try
                    {
                        coverage = Coverage.GetCoverage(qHeader.SessionId);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        coverage = new Coverage();
                        coverage.SessionId = qHeader.SessionId;
                    }
                }

                return coverage;
            }
            set
            {
                coverage = value;
            }
        }

        Quote IBusinessLogicVisitable.Quote
        {
            get
            {
                if (quote == null)
                    this.quote = Quote.GetQuote(qHeader.SessionId);

                return quote;
            }
            set
            {
                quote = value;
            }
        }

        Header IBusinessLogicVisitable.Header
        {
            get
            {
                if (header == null)
                {
                    try
                    {
                        header = Header.GetHeader(qHeader.SessionId);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        header = new Header();
                        header.SessionID = qHeader.SessionId;
                    }
                }

                return header;
            }
            set
            {
                header = value;
            }
        }

        AvailableCredit IBusinessLogicVisitable.Credit
        {
            get
            {
                if (credit == null)
                {
                    try
                    {
                        credit = AvailableCredit.GetAvailableCredit(qHeader.SessionId);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        credit = new AvailableCredit();
                        credit.SessionId = qHeader.SessionId;
                    }

                }

                return credit;
            }
            set
            {
                credit = value;
            }
        }

        Animals IBusinessLogicVisitable.Animals
        {
            get
            {
                if (animals == null)
                    animals = Animals.GetAnimals(qHeader.SessionId);

                return animals;
            }
            set
            {
                animals = value;
            }
        }

        Claims IBusinessLogicVisitable.Claims
        {
            get
            {
                if (claims == null)
                    claims = Claims.GetClaims(qHeader.SessionId);

                return claims;
            }
            set { claims = value; }
        }

        CreditCard IBusinessLogicVisitable.CreditCard
        {
            get
            {
                if (creditCard == null)
                {

                    CreditCards cards =
                        CreditCards.GetCreditCards(qHeader.SessionId);

                    if (cards.Count == 1)
                    {
                        creditCard = cards[0];
                    }
                    else if (cards.Count == 0)
                    {
                        creditCard = new CreditCard();
                        creditCard.SessionId = QHeader.SessionId;
                        creditCard.CustType = "PRIMARY";
                    }
                    else
                    {
                        throw new Exception("More then 1 cc...");
                    }
                }

                return creditCard;
            }
            set
            {
                creditCard = value;
            }
        }

        PaymentDetail IBusinessLogicVisitable.Payment
        {
            get
            {
                if (payment == null)
                {
                    try
                    {
                        payment = PaymentDetail.GetPaymentDetail(QHeader.SessionId);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        payment = new PaymentDetail { SessionId = QHeader.SessionId, PaymentType = "UNKNOWN" };
                    }

                }
                return payment;
            }
            set
            {
                payment = value;
            }

        }

        EFTs IBusinessLogicVisitable.EFTs
        {
            get
            {
                if (eFTs == null)
                    eFTs = EFTs.GetEFTs(qHeader.SessionId);

                return eFTs;
            }
            set
            {
                eFTs = value;
            }
        }

        Endorsements IBusinessLogicVisitable.Endorsements
        {
            get
            {
                if (endorsements == null)
                    this.endorsements = Endorsements.GetEndorsements(qHeader.SessionId);

                return endorsements;
            }
            set
            {
                endorsements = value;
            }
        }

        FaxDetails IBusinessLogicVisitable.FaxDetails
        {
            get
            {
                if (faxDetails == null)
                    this.faxDetails = FaxDetails.GetFaxDetails(qHeader.SessionId);

                return faxDetails;
            }
            set
            {
                faxDetails = value;
            }
        }

        HO0448s IBusinessLogicVisitable.HO0448s
        {
            get
            {
                if (hO0448s == null)
                    hO0448s = HO0448s.GetHO0448s(qHeader.SessionId);

                return hO0448s;
            }
            set
            {
                hO0448s = value;
            }
        }

        HomeDayCare IBusinessLogicVisitable.HomeDayCare
        {
            get
            {
                if (homeDayCare == null)
                {
                    try
                    {
                        homeDayCare = HomeDayCare.GetHomeDayCare(qHeader.SessionId);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        homeDayCare = new HomeDayCare();
                        homeDayCare.SessionId = QHeader.SessionId;
                    }
                }

                return homeDayCare;
            }
            set
            {
                homeDayCare = value;
            }
        }

        Mortgages IBusinessLogicVisitable.Mortgages
        {
            get
            {
                if (mortgages == null)
                    this.mortgages = Mortgages.GetMortgages(qHeader.SessionId);

                return mortgages;
            }
            set
            {
                mortgages = value;
            }
        }

        ScheduledPersonalPropreties IBusinessLogicVisitable.ScheduledPersonalPropreties
        {
            get
            {
                if (scheduledPersonalPropreties == null)
                    scheduledPersonalPropreties = ScheduledPersonalPropreties.GetScheduledPersonalPropreties(qHeader.SessionId);

                return scheduledPersonalPropreties;
            }
            set
            {
                scheduledPersonalPropreties = value;
            }
        }

        ThankYouDetail IBusinessLogicVisitable.ThankYouDetail
        {
            get
            {
                if (thankYouDetail == null)
                {
                    try
                    {
                        thankYouDetail = ThankYouDetail.GetThankYouDetail(this.BusinessLogic().Quote.CompanysPolicyNumber);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        thankYouDetail = new ThankYouDetail();
                        thankYouDetail.PolicyNumber = this.BusinessLogic().Quote.CompanysPolicyNumber;
                    }
                }

                return thankYouDetail;
            }
            set
            {
                thankYouDetail = value;
            }
        }

        FloorTypes IBusinessLogicVisitable.FloorTypes
        {
            get
            {
                if (floorTypes == null)
                    floorTypes = FloorTypes.GetFloorTypes(qHeader.SessionId);

                return floorTypes;
            }
            set { floorTypes = value; }
        }

        WallTypes IBusinessLogicVisitable.WallTypes
        {
            get
            {
                if (wallTypes == null)
                    wallTypes = WallTypes.GetWallTypes(qHeader.SessionId);

                return wallTypes;
            }
            set { wallTypes = value; }
        }

        FireplaceTypes IBusinessLogicVisitable.FireplaceTypes
        {
            get
            {
                if (fireplaceTypes == null)
                    fireplaceTypes = FireplaceTypes.GetFireplaceTypes(qHeader.SessionId);

                return fireplaceTypes;
            }
            set { fireplaceTypes = value; }
        }

        QualityGroups IBusinessLogicVisitable.QualityGroup
        {
            get
            {
                if (qualityGroup == null)
                    qualityGroup = QualityGroups.GetQualityGroup(qHeader.SessionId);

                return qualityGroup;
            }
            set { qualityGroup = value; }
        }

        HomeBusinessDetail IBusinessLogicVisitable.HomeBusinessDetail
        {
            get
            {
                if (homeBusinessDetail == null)
                    homeBusinessDetail = HomeBusinessDetail.GetHomeBusinessDetail(qHeader.SessionId);

                return homeBusinessDetail;
            }
            set { homeBusinessDetail = value; }
        }

        AutoClaims IBusinessLogicVisitable.AutoClaims
        {
            get
            {
                if (autoClaims == null)
                    autoClaims = AutoClaims.GetAutoClaims(qHeader.SessionId);

                return autoClaims;
            }
            set { autoClaims = value; }
        }

        private PackageCoverages packageCoverages;
        /// <summary>
        /// Gets or sets the package coverages.
        /// </summary>
        /// <value>
        /// The package coverages.
        /// </value>
        /// 
      
        public PackageCoverages PackageCoverages
        {
            get
            {
                if (packageCoverages == null)
                    packageCoverages = PackageCoverages.GetPackageCoverages(qHeader.SessionId);

                return packageCoverages;
            }
            set
            {
                packageCoverages = value;
            }
        }

        QuoteData IBusinessLogicVisitable.QData
        {
            get { return qData; }
            set { qData = value; }
        }

        QuoteHeader IBusinessLogicVisitable.QHeader
        {
            get { return qHeader; }
            set { qHeader = value; }
        }

        PurchaseData pData;

        PurchaseData IBusinessLogicVisitable.PData
        {
            get
            {
                return this.pData;
            }
            set
            {
                this.pData = value;
            }
        }


        #endregion

        #region IBusinessLogic Members

        public QuoteHeader QHeader
        {
            get
            {
                return qHeader;
            }
            set
            {
                qHeader = value;
            }
        }

        public QuoteData QData
        {
            get
            {
                return qData;
            }
            set
            {
                qData = value;
            }
        }

        public ICoverageInputData InputData
        {
            get;
            set;
        }

        public PurchaseData PData
        {
            get
            {
                return pData;
            }
            set
            {
                pData = value;
            }
        }        

        public void InterpretResult(AjaxResponse response)
        {
            AddErrorToResponse(response);
            AddMessageToResponse(response);
            AddDataSourcesToResponse(response);

            if (this.BusinessLogic().Quote.OriginalQuotationDate != null)
            {
                response.quoteHeader.OriginalQuoteDate =
                    Convert.ToString(this.BusinessLogic().Quote.OriginalQuotationDate);
            }

            // parse quote step list passed from UI so that we can pass back
            // appropriate navigational info
            var nextStep = GetNextStep(response);

            var isStepBeforeCoverages = IsStepBeforeCoverages();

            if (IsIneligible)
            {
                DirectWebDAC.SessionExperience(response.quoteHeader.SessionId,
                    response.quoteHeader.FormCode.ToString(),
                    TranslateStepForOldSessionExperience(response.quoteHeader.Step),
                    "InEligible");

                response.Status = ResponseStatus.Ineligible;
                response.RedirectURL = "InEligible.aspx";
            }
            else if (IsDnq)
            {

                DirectWebDAC.SessionExperience(response.quoteHeader.SessionId,
                    response.quoteHeader.FormCode.ToString(),
                    TranslateStepForOldSessionExperience(response.quoteHeader.Step),
                    "DNQ");

                response.Status = ResponseStatus.DNQ;
                response.RedirectURL = "DNQ.aspx";
            }
            else if (IsContactCustomerCare)
            {
                DirectWebDAC.SessionExperience(response.quoteHeader.SessionId,
                 response.quoteHeader.FormCode.ToString(),
                 TranslateStepForOldSessionExperience(response.quoteHeader.Step),
                 "ContactCustomerCare");

                response.Status = ResponseStatus.ContactCustomerCare;
                response.RedirectURL = "ContactCustomerCare.aspx";
            }
            else if (displayAPlusModal)
            {
                response.NextPanel = AjaxPanel.APlusClaim;
            }
            else if (!IsAddressVerified &&
                !string.IsNullOrEmpty(this.BusinessLogic().UserPropertyAddress.Addr1))
            {
                CheckAddressVerification(response);
            }
            else
            {
                if (response.quoteHeader.Step == "Your Address")
                {
                    response.RedirectURL = "iQuote.aspx";
                }
                else if (isStepBeforeCoverages)
                {

                    if (ShowForm2Popup())
                    {
                        response.NextPanel = AjaxPanel.OnlyForm2_Popup;
                        this.BusinessLogic().QData.marketValueFlow = 1;
                        this.BusinessLogic().Quote.FormNumber = "2";
                        this.BusinessLogic().Header.FormNumber = 2;
                        this.QHeader.FormCode = 2;
                        this.DataBinder.SaveDataObjects();

                    }
                    else if (QuoteServices.ValidPrimaryPolicy(this.quote.PrimaryPolicyNumber, this.QHeader.FormCode, this.BusinessLogic().Structure.DwellingUseCd, this.BusinessLogic().Header.State, GetPrimaryInsuredByHomesite()) == false)
                    {
                        if (response.quoteHeader.Step != "p5")
                        {
                            response.errorMessage = "The policy number entered is invalid. Review your policy documentation and try again.";
                            response.errorOccured = true;
                        }
                        response.Status = ResponseStatus.InvalidInput;
                        response.NextPanel = AjaxPanel.InvalidData;
                    }
                    else
                    {
                        if (String.IsNullOrEmpty(response.quoteHeader.MandSerror))
                        {
                            response.NextPanel = GetAjaxPanelFromString(nextStep);
                        }
                        else
                        {
                            response.NextPanel = AjaxPanel.Property_Info;
                        }

                        if (QuoteServices.IsFeeState(this.QHeader.State) &&
                            (this.QHeader.FormCode == 6 || this.QHeader.FormCode == 4))
                        {
                            response.message = "Quote saved successfully! Your Quote Number is " + this.qHeader.QuoteNumber +
                           ".  Please click on 'View Quote' link to download a PDF file that contains all the quote details.";
                        }

                    }
                }
                else if (response.quoteHeader.Step == "Coverage" || response.quoteHeader.Step == "APlusClaim")
                {
                    //ITR#6988 Go to Error page when CovE, CovF and Deductible are null or 0 and COvA exists
                    if (((this.BusinessLogic().Coverage.CovEAmount == 0) || (this.BusinessLogic().Coverage.CovEAmount == null)) && ((this.BusinessLogic().Coverage.CovFAmount == 0) || (this.BusinessLogic().Coverage.CovFAmount == null)) && ((string.IsNullOrEmpty(this.BusinessLogic().Coverage.Deductible))) && (this.BusinessLogic().Coverage.CovAAmount != null || (this.BusinessLogic().Coverage.CovAAmount != 0)))
                    {
                        response.NextPanel = AjaxPanel.Error;
                        response.errorMessage = "Deductible is Null";

                        ECommerceServiceLibaryLogger log = Homesite.Diagnostics.ECommerce.ECommerceServiceLibaryLogger.GetInstance;
                        log.Trace(-6, response.errorMessage, qHeader.SessionId);

                        response.quoteHeader.SessionId = 0;
                        response.RedirectURL = "Error.aspx";
                    }
                    else
                    {
                        response.NextPanel = AjaxPanel.Purchase;
                    }

                }
                else
                {
                    response.NextPanel = GetAjaxPanelFromString(nextStep);
                    response.NextPanelName = nextStep;

                    InterpretEligibilityExpansion(response);

                    if (nextStep == "iQuote") // FOR LEGACY ONLY
                    {
                        SocialSecurityNumber();
                        if (displaySSNModal)
                        {
                            response.NextPanel = AjaxPanel.SSN_Popup;
                            response.message = null;
                        }
                    }


                }

                // call ssn logic before coverages.
                //if (this.IQuote().PrimaryPerson != null && this.IQuote().PrimaryPerson.TransUnionRequestID != null && this.IQuote().PrimaryPerson.SocialSecurityNumber != null)
                if (response.NextPanel == AjaxPanel.Coverage)
                {
                    SocialSecurityNumber();

                    if (displaySSNModal)
                    {
                        response.NextPanel = AjaxPanel.SSN_Popup;
                        response.message = null;
                    }
                }
            }

            response.quoteHeader.IsCatZoneZip = DirectWebDAC.IsCatZoneZip(this.QHeader.Zip, this.QHeader.State);
            //ITR#8945
            response.quoteHeader.IsHomeBusinessEndorsementAvailable = ContactCustomerCareProcessor.GetIsHomeBusinessEndorsementAvailableFlag();

            SetQuoteHeaderForResponse(response);

            response.QuoteData = this.QData;

            SessionExperienceLog(response);


            System.Diagnostics.Debug.WriteLine("* End: Interpret Result");
        }
	


        private bool? GetPrimaryInsuredByHomesite()
        {
            bool? primaryInsuredByHomesite;
            if (this.BusinessLogic().QData.primaryInsuredByHomesite == null)
            {
                primaryInsuredByHomesite = null;
            }
            else if (this.BusinessLogic().QData.primaryInsuredByHomesite == YesNo.Yes)
            {
                primaryInsuredByHomesite = true;
            }
            else
            {
                primaryInsuredByHomesite = false;
            }
            return primaryInsuredByHomesite;
        }

        private void InterpretEligibilityExpansion(AjaxResponse response)
        {
            // ITR#4415 Eligibility Expansion

            // For Eligibility Expansion, if the user is on the Property Info page
            // but one of the FAHit values is 2, this means that the user just selected
            // that they would like to send documentation to support their claims.
            if (response.quoteHeader.Step == "Property Info")
            {
                if (qData.FAHitMV == 2 || qData.FAHitSF == 2 || qData.FAHitYB == 2)
                {
                    // QC 7686 and 7657
                    this.ServiceInterface.CallSaveQuoteService();

                    response.RedirectURL = "AppraisalDocumentation.aspx";
                }
            }


            // If the user chooses to trigger an appraisal,
            // gets to the AppraisalDocumentation screen,
            // then decides to cancel by pressing the "Back" button
            // we need to end up back on the Property Info screen
            if (this.QHeader.FormCode == 3)
            {
                // QC#7668;4415 FAHit field should contain the TaxRetrievalID
                if (qData.FAHitMV == 2 || qData.FAHitSF == 2 || qData.FAHitYB == 2)
                {
                    response.NextPanel = GetAjaxPanelFromString("Property_Info");
                    response.NextPanelName = "Property_Info";
                }
            }
        }

        //private void CheckAddressVerification(AjaxResponse response)
        //{
        //    if (this.BusinessLogic().PropertyAddressBrokerOutput.MatchCode.Substring(0, 1).Equals("Z") ||
        //        (this.BusinessLogic().PropertyAddressBrokerOutput.MatchCode.Substring(0, 1).Equals("E") && this.BusinessLogic().PropertyAddressBrokerOutput.LocationQualityCode.Substring(0, 1).Equals("Z")))
        //    {
        //        response.NextPanel = AjaxPanel.Address_NoMatch;
        //    }
        //    else
        //    {
        //        response.NextPanel = AjaxPanel.Address_PartialMatch;
        //    }

        //    if ((response.NextPanel == AjaxPanel.Address_NoMatch) || (response.NextPanel == AjaxPanel.Address_PartialMatch))
        //    {
        //        DataBinder.RefreshDataObjects();
        //        qData.userPropertyAddressLine1 = this.BusinessLogic().UserPropertyAddress.Addr1;
        //        qData.userPropertyAddressLine2 = this.BusinessLogic().UserPropertyAddress.Addr2;
        //        qData.userPropertyCity = this.BusinessLogic().UserPropertyAddress.City;
        //        qData.userPropertyState = this.BusinessLogic().UserPropertyAddress.StateProvCd;
        //        qData.userPropertyZip = this.BusinessLogic().UserPropertyAddress.PostalCode;
        //        qData.propertyAddressLine1 = this.BusinessLogic().PropertyAddress.Addr1;
        //        qData.propertyAddressLine2 = this.BusinessLogic().PropertyAddress.Addr2;
        //        qData.propertyCity = this.BusinessLogic().PropertyAddress.City;
        //        qData.propertyState = this.BusinessLogic().PropertyAddress.StateProvCd;
        //        qData.propertyZip = this.BusinessLogic().PropertyAddress.PostalCode;
        //    }
        //}

        private string GetNextStep(AjaxResponse response)
        {
            string currentStep = response.quoteHeader.Step.Replace(' ', '_');
            string nextStep = string.Empty;
            bool saveNext = false;
            if (!string.IsNullOrEmpty(response.quoteHeader.QuoteSteps))
            {
                this.QuoteSteps = new List<string>();
                foreach (string step in response.quoteHeader.QuoteSteps.Split(','))
                {
                    if (!string.IsNullOrEmpty(step))
                    {
                        this.QuoteSteps.Add(step);
                        if (saveNext)
                        {
                            nextStep = step;
                            //if (nextStep != "iQuote")
                            //{
                            saveNext = false;
                            //}
                        }
                        else if (currentStep == step)
                        {
                            saveNext = true;
                        }
                        if (this.BusinessLogic().QHeader.H06Ui_V2 && this.BusinessLogic().QHeader.State != "MD")
                        {
                            currentStep = nextStep;
                            saveNext = true;
                        }
                    }
                }
            }
            return nextStep;
        }

        private void AddDataSourcesToResponse(AjaxResponse response)
        {
            // check for data sources
            if (controlData.Count > 0)
            {
                foreach (ControlDataSource cds in controlData)
                {
                    response.DataSource.Add(cds);
                }

                controlData.Clear();
            }
        }

        private void AddMessageToResponse(AjaxResponse response)
        {
            if (this.InfoMessage.Length != 0)
            {
                response.message = this.InfoMessage;
            }
            else if (ServiceInterface.InfoMessage.Length != 0)
            {
                response.message = ServiceInterface.InfoMessage;
            }
        }

        private void AddErrorToResponse(AjaxResponse response)
        {
            if (this.ErrorMessage.Length == 0)
            {
                response.Status = ResponseStatus.Success;
            }
            else
            {
                response.errorOccured = true;
                response.errorMessage = this.ErrorMessage;
            }
        }

        private void SessionExperienceLog(AjaxResponse response)
        {
            IQuoteConfiguration configuration = IQuoteConfiguration.GetConfigurationInstance();
            Homesite.IQuote.SessionBroker.SessionProvider session = new SessionProvider();

            bool logPost = false;
            Boolean.TryParse(configuration.Setting("LogPOST"), out logPost);
            if (logPost)
            {
                // attempting to log the same way as session experience...
                if (string.IsNullOrEmpty(response.RedirectURL) && response.NextPanelName != "Next")
                {
                    Homesite.ECommerce.DAC.DirectWebDAC.SessionExperience(response.quoteHeader.SessionId,
                        response.quoteHeader.FormCode.ToString(),
                        TranslateStepForOldSessionExperience(response.quoteHeader.Step),
                        TranslateStepForOldSessionExperience(response.NextPanelName));
                }
                else if (response.RedirectURL == "iQuote.aspx")
                {
                    Homesite.ECommerce.DAC.DirectWebDAC.SessionExperience(response.quoteHeader.SessionId,
                        response.quoteHeader.FormCode.ToString(),
                        TranslateStepForOldSessionExperience(response.quoteHeader.Step),
                        TranslateStepForOldSessionExperience("About_You"));
                }
                else if (response.quoteHeader.Step == "Welcome" && response.NextPanelName == "Next")
                {
                    Homesite.ECommerce.DAC.DirectWebDAC.SessionExperience(response.quoteHeader.SessionId,
                        response.quoteHeader.FormCode.ToString(),
                        TranslateStepForOldSessionExperience(response.quoteHeader.Step),
                        TranslateStepForOldSessionExperience("Your_Address"));
                }
                else if ((response.quoteHeader.Step == "About You" || response.quoteHeader.Step == "Property Info" || response.quoteHeader.Step == "Additional Info") && response.NextPanelName == "Next")
                {
                    Homesite.ECommerce.DAC.DirectWebDAC.SessionExperience(response.quoteHeader.SessionId,
                        response.quoteHeader.FormCode.ToString(),
                        TranslateStepForOldSessionExperience(response.quoteHeader.Step),
                        "DataCollection1");
                }
            }
        }

        private void SetQuoteHeaderForResponse(AjaxResponse response)
        {
            if (!string.IsNullOrEmpty(this.BusinessLogic().PropertyAddress.Addr1))
            {
                this.QHeader.Addr1 = this.BusinessLogic().PropertyAddress.Addr1;
                this.QHeader.Addr2 = this.BusinessLogic().PropertyAddress.Addr2;
                this.QHeader.City = this.BusinessLogic().PropertyAddress.City;
                this.QHeader.State = this.BusinessLogic().PropertyAddress.StateProvCd;
                this.QHeader.Zip = this.BusinessLogic().PropertyAddress.PostalCode;
            }

            response.quoteHeader = this.BusinessLogic().QHeader;


            if (this.BusinessLogic().Quote.CompanysQuoteNumber != null)
            {
                response.quoteHeader.QuoteNumber = this.BusinessLogic().Quote.CompanysQuoteNumber;
            }

            response.quoteHeader.CompleteQuoteFlag =
                (int) (this.BusinessLogic().Quote.CompleteQuoteFlag ?? 0);

            if (this.BusinessLogic().Quote.PolicyEffectiveDate != null)
            {
                response.quoteHeader.PolicyEffectiveDate =
                    ((DateTime) this.BusinessLogic().Quote.PolicyEffectiveDate).ToString("MM/dd/yyyy");
            }

            if (String.IsNullOrEmpty(this.BusinessLogic().Quote.ApexBillingOption))
            {
                response.quoteHeader.ApexBillingOption = this.BusinessLogic().Quote.ApexBillingOption = string.Empty;
            }
            else
            {
                response.quoteHeader.ApexBillingOption = this.BusinessLogic().Quote.ApexBillingOption;

            }
        }

        private string TranslateStepForOldSessionExperience(string step)
        {
            string retv;
            switch (step)
            {
                case "Welcome":
                    retv = "InitialQuestions1";
                    break;
                case "Your_Address":
                case "Your Address":
                    retv = "InitialQuestions2";
                    break;
                case "About_You":
                case "About You":
                case "Property_Info":
                case "Property Info":
                case "Additional_Info":
                case "Additional Info":
                    retv = "DataCollection1";
                    break;
                case "Coverage":
                    retv = "PremiumCustomization";
                    break;
                case "APlusClaim":
                    retv = "APlusClaims";
                    break;
                case "Purchase":
                    retv = "PaymentMethod";
                    break;
                default:
                    retv = step;
                    break;
            }
            return retv;
        }


        public BillingOptions GetBillingOptions(Homesite.IQuote.IndicativeQuote.IQuote iQuote)
        {
            return new BillingOptions();
        }

        public QuoteData GetValuesFromSession(QuoteHeader qHeader)
        {
            this.BusinessLogic().QHeader = qHeader;

            return this.DataBinder.SessionDatabaseToQuoteData();
        }

        public QuoteData GetDefaultValues(QuoteHeader qHeader)
        {
            throw new NotImplementedException();
        }


        public AjaxResponse Purchase(QuoteHeader qHeader, PurchaseData pData)
        {
            //ITR#7841 Horison Audits - Web 2.0 Time-out 
            AjaxResponse response = new AjaxResponse();
            if (Convert.ToDateTime(DataBinder.QHeader.PolicyEffectiveDate) <= DateTime.Today)
            {

                response.quoteHeader = qHeader;
                response.quoteHeader.SystemDate = DateTime.Today.ToString();
                response.quoteHeader.ExpiredQuote = true;
                response.quoteHeader.IsRetrieve = false;
                if (qHeader.FormCode == 3)
                {
                    response.NextPanel = AjaxPanel.Your_Address;
                    response.quoteHeader.LandingStep = "Your_Address";
                    response.RedirectURL = "BeginQuote.aspx";
                }
                else if (qHeader.FormCode == 6)
                {
                    response.NextPanel = AjaxPanel.Condo_Information;
                    response.RedirectURL = "iQuote.aspx";
                }
                else if (qHeader.FormCode == 4)
                {
                    response.NextPanel = AjaxPanel.About_You;
                    response.RedirectURL = "iQuote.aspx";
                }

            }
            else
            {

                // ITR#7455 Adding Logging for DwellingUseCd and EstimatedReplCost.
                string logDwellingMessage;
                if (this.DataBinder.Structure.DwellingUseCd == null)
                {
                    logDwellingMessage = "DwellingUseCd was Null prior to the refresh, ";
                }
                else
                {
                    logDwellingMessage = "DwellingUseCd was " + this.DataBinder.Structure.DwellingUseCd.ToString() +
                                         " prior to the refresh, ";
                }

                string logReplacementCostMessage;
                if (this.DataBinder.Coverage.EstimatedReplCostAmt == null)
                {
                    logReplacementCostMessage = "EstimatedReplCostAmt was Null prior to the refresh, ";
                }
                else
                {
                    logReplacementCostMessage = "EstimatedReplCostAmt was " +
                                                this.DataBinder.Coverage.EstimatedReplCostAmt.ToString() +
                                                " prior to the refresh, ";
                }


                // Refresh the DataBinder so that if we are coming from a different server we have the latest from the database.
                this.DataBinder.RefreshDataObjects();

                this.Log("Processing Purchase - Start... Inspection Result value = " +
                         this.BusinessLogic().Quote.InspectionResult.ToString());
                //this.quote = null;  //Added For inspection 
                this.BusinessLogic().Quote = null;
                this.Log("Processing Purchase - After refreshing the quote object... Inspection Result value = " +
                         this.BusinessLogic().Quote.InspectionResult.ToString());

                this.ErrorMessage = string.Empty;

                //AjaxResponse response = new AjaxResponse();

                #region Legacy Business Logic Ported code

                this.PData = pData;

                this.PurchaseProcessor.SaveConsentToRate(out this.ErrorMessage);

                this.PurchaseProcessor.SavexWind(out this.ErrorMessage); // ITR#7684

                this.PurchaseProcessor.SaveConsentToRateAndxWind(out this.ErrorMessage); // ITR#7684

                if (string.IsNullOrEmpty(this.ErrorMessage))
                {
                    this.PurchaseProcessor.SavePhoneNumber();
                    this.PurchaseProcessor.SaveMailingAddress();
                    this.PurchaseProcessor.SaveMortgageInfo();

                    if (pData.routingNumber != null && !PurchaseProcessor.ValidateEFT(pData.routingNumber))
                    {

                        this.ErrorMessage =
                            "<strong>There is a problem with your answers below. We may have some missing or inaccurate information. Please check the following field(s):</strong><a href='#RoutingNumber'>Routing Number</a>";

                        this.Log("Processing Purchase - Payment = AutoCheck... Inspection Result value = " +
                                 this.BusinessLogic().Quote.InspectionResult.ToString());
                    }
                    if (string.IsNullOrEmpty(ErrorMessage))
                    {
                        this.PurchaseProcessor.SavePaymentDetails();
                        // ITR#7455 Added logging for the Early Cash issue 
                        if (this.DataBinder.Structure.DwellingUseCd == null)
                        {
                            logDwellingMessage += "and DwellingUseCd was Null after to the refresh.";
                        }
                        else
                        {
                            logDwellingMessage += "and DwellingUseCd was " +
                                                  this.DataBinder.Structure.DwellingUseCd.ToString() +
                                                  " after to the refresh.";
                        }

                        if (this.DataBinder.Coverage.EstimatedReplCostAmt == null)
                        {
                            logReplacementCostMessage += "and EstimatedReplCostAmt was Null after to the refresh,";
                        }
                        else
                        {
                            logReplacementCostMessage += "and EstimatedReplCostAmt was " +
                                                         this.DataBinder.Coverage.EstimatedReplCostAmt.ToString() +
                                                         " after to the refresh";
                        }

                        // Log the above results
                        this.Log(logDwellingMessage);
                        this.Log(logReplacementCostMessage);

                        this.DataBinder.SaveDataObjects();

                        this.Log("Processing Purchase - Data Objects Saved... Inspection Result value = " +
                                 this.BusinessLogic().Quote.InspectionResult.ToString());

                        if (this.BusinessLogic().Quote.APLUSStatus == null)
                        {
                            this.BusinessLogic().Quote.APLUSStatus = 2;
                            this.BusinessLogic().Quote.Save();
                            this.Log("Processing Purchase - Quote Saved... Inspection Result value = " +
                                     this.BusinessLogic().Quote.InspectionResult.ToString());
                        }

                    }

                #endregion

                    #region Legacy Progressive Business Logic Ported Code
                }

                if (string.IsNullOrEmpty(this.ErrorMessage))
                {
                    this.DataBinder.RefreshDataObjects();

                    if (!this.ServiceInterface.CallSaveQuoteService())
                    {
                        response.errorOccured = true;
                        response.errorMessage = "Error processing payment details. (save quote)";
                        response.NextPanel = AjaxPanel.Purchase;
                        this.Log("Processing Purchase - CallSaveQuoteService() = False... Inspection Result value = " +
                                 this.BusinessLogic().Quote.InspectionResult.ToString());
                        return response;
                    }

                    if (quote.APLUSStatus != 1)
                    {
                        //bool convertEnabled = this.PurchaseProcessor.ConvertEnabled();
                        //if (convertEnabled)
                        this.ServiceInterface.CallConvertService();
                        this.Log("PartnerId: " + this.BusinessLogic().QHeader.PartnerId + " CompanyPolicyNumber: " +
                                 quote.CompanysPolicyNumber + " OLSStatus: " + this.BusinessLogic().QHeader.OLSStatus);
                        if (this.BusinessLogic().QHeader.PartnerId == 2319 &&
                            !string.IsNullOrEmpty(quote.CompanysPolicyNumber) &&
                            this.BusinessLogic().QHeader.OLSStatus == OlsStatusPasswordVerified)
                        {
                            EnrollmentServiceClient enrollmentService = null;
                            try
                            {
                                this.Log("Enrollment Service Being called");
                                string email =
                                    DirectWebDAC.GetOLSEnrollmentValues(qHeader.QuoteNumber).Rows[0]["EmailAddress"].
                                        ToString();
                                enrollmentService = new EnrollmentServiceClient();
                                EnrollForSelfServiceRequest request = new EnrollForSelfServiceRequest();
                                request.PolicyNumber = quote.CompanysPolicyNumber;
                                request.Channel = EnrollmentChannel.ConsumerWeb;
                                PasswordProvider provider = new PasswordProvider();
                                provider.LoginId = email;
                                provider.Password = this.BusinessLogic().PData.password;
                                request.Authentication = provider;
                                request.RequestId =
                                    DirectWebDAC.GetOLSEnrollmentValues(qHeader.QuoteNumber).Rows[0]["Token"].ToString();
                                request.RequestType = OLSService.ValidationRequestType.Token;
                                request.TransactionID = Guid.NewGuid();
                                var result = enrollmentService.EnrollForSelfService(request);
                                if (result.Result)
                                {
                                    this.Log("Enrollment into OLS was sucessful. Policy Number: " +
                                             this.BusinessLogic().Quote.CompanysPolicyNumber);
                                    this.BusinessLogic().QHeader.OLSStatus = OlsStatusReadyToEnroll;
                                }
                                else
                                {
                                    this.Log("Enrollment into OLS was unsucessful. Error Message: " +
                                             result.ErrorMessage);
                                }

                            }
                            catch (Exception e)
                            {
                                this.Log("Error with enrollmentservice: " + e.Message);
                            }
                            finally
                            {
                                if (enrollmentService != null)
                                    enrollmentService.Close();
                                this.Log("Enrollment Service ended OLSStatus: " + this.BusinessLogic().QHeader.OLSStatus);
                            }




                        }
                        this.Log("Processing Purchase - After CallConvertService... Inspection Result value = " +
                                 this.BusinessLogic().Quote.InspectionResult.ToString());
                    }
                    else
                    {
                        throw new Exception("Fix Me.");

                    }
                }

                    #endregion

                this.DataBinder.RefreshDataObjects();
                this.Log("Processing Purchase - After RefreshDataObjects... Inspection Result value = " +
                         this.BusinessLogic().Quote.InspectionResult.ToString());

                // interpret results...
                if (this.ErrorMessage.Length == 0 && this.ServiceInterface.ErrorMessage.Length == 0 &&
                    this.PurchaseProcessor.ErrorMessage.Length == 0)
                {
                    response.NextPanel = AjaxPanel.Thank_You;
                    this.Log("Processing Purchase - Thank You... Inspection Result value = " +
                             this.BusinessLogic().Quote.InspectionResult.ToString());
                }
                else
                {
                    response.NextPanel = AjaxPanel.Purchase;

                    if (this.ErrorMessage.Length != 0)
                        response.errorMessage = this.ErrorMessage;
                    else if (this.ServiceInterface.ErrorMessage.Length != 0)
                        response.errorMessage = this.ServiceInterface.ErrorMessage;
                    else if (this.PurchaseProcessor.ErrorMessage.Length != 0)
                        response.errorMessage = this.PurchaseProcessor.ErrorMessage;

                    response.errorOccured = true;
                    this.Log("Processing Purchase - Error... Inspection Result value = " +
                             this.BusinessLogic().Quote.InspectionResult.ToString());
                    if (response.errorMessage.Trim().Length > 0)
                        this.Log("Processing Purchase - Error Description : " + response.errorMessage.Trim());

                }

                // check for policy servicing redirect after successful purchase
                if (response.NextPanel == AjaxPanel.Thank_You)
                {
                    IQuoteConfiguration configuration = IQuoteConfiguration.GetConfigurationInstance();

                    // If user OLS Status == 5 then user is enrolled in OLS
                    // this is for Walmart Associate
                    if (qHeader.OLSStatus == OlsStatusReadyToEnroll && !(DirectWebDAC.IsBatchRunning()))
                    {
                        this.Log("Processing Purchase - OnlineServicingEnabled... Inspection Result value = " +
                                 this.BusinessLogic().Quote.InspectionResult.ToString());
                        if (this.PurchaseProcessor.WritePolicyServicingTransferData())
                        {
                            this.Log(
                                "Processing Purchase - WritePolicyServicingTransferData() = True... Inspection Result value = " +
                                this.BusinessLogic().Quote.InspectionResult.ToString());
                            //Redirect to OLSEnrolled page instead of OnlineServicing thank you page
                            StringBuilder redirUrl = this.PurchaseProcessor.BuildOLSEnrolledRedirectUrl();
                            response.RedirectURL = redirUrl.ToString();
                        }

                    }
                    else if (configuration.FunctionalityEnabled("OnlineServicingEnabled") && !(DirectWebDAC.IsBatchRunning()))
                    {
                        this.Log("Processing Purchase - OnlineServicingEnabled... Inspection Result value = " +
                                 this.BusinessLogic().Quote.InspectionResult.ToString());
                        if (this.PurchaseProcessor.WritePolicyServicingTransferData())
                        {
                            this.Log(
                                "Processing Purchase - WritePolicyServicingTransferData() = True... Inspection Result value = " +
                                this.BusinessLogic().Quote.InspectionResult.ToString());
                            StringBuilder redirUrl = this.PurchaseProcessor.BuildOnlineServicingRedirectUrl();
                            response.RedirectURL = redirUrl.ToString();
                        }
                    }
                }

                this.Log("Processing Purchase - End of Purchase... Inspection Result value = " +
                         this.BusinessLogic().Quote.InspectionResult.ToString());
            }

            return response;

        }

        /// <summary>
        /// Because a Policy Number is created and stored in the database (HomesiteWeb..hs_quote) when the purchase button is clicked but before
        /// the purchase is successful, we cannot use only the existence of a Policy Number as indication of a purchased policy.  QuoteStatus of
        /// 6 will indicate a successful purchase.  Both will be checked in order to verify that this quote has a purchased policy.
        /// Ref. ITR#9394
        /// 4 is Early Cash successful and also indicates purchased policy while Batch is running
        /// </summary>
        /// <param name="qHeader">QuoteHeader with a populated SessionId property</param>
        /// <returns></returns>
        public bool PurchasedPolicy(QuoteHeader qHeader)
        {
            this.BusinessLogic().Quote =
                Quote.GetQuote(this.BusinessLogic().QHeader.SessionId);

            if (((this.BusinessLogic().Quote.QuoteStatus != 4)
                && (this.BusinessLogic().Quote.QuoteStatus != 6)) // ITR#9394 Adding this. Quote Status of 6 means successful purchase
                || (string.IsNullOrEmpty(this.BusinessLogic().Quote.CompanysPolicyNumber))) // May have Policy Number without successful purchase
                return false;
            else
                return true;
        }

        private void SocialSecurityNumber()
        {
            int pid = this.BusinessLogic().QHeader.PartnerId;
            Homesite.IQuote.IndicativeQuote.SiteTypes st = Homesite.IQuote.IndicativeQuote.SiteTypes.Consumer;
            int fc = this.BusinessLogic().QHeader.FormCode;
            string state = this.BusinessLogic().QHeader.State;
            string ssn = this.BusinessLogic().PrimaryPerson.SocialSecurityNumber.Replace("_", "").Replace("-", "");
            string effd = this.BusinessLogic().Coverage.PolicyEffDate;
            int pt = Convert.ToInt16(this.BusinessLogic().Header.ProgramType);
            int tu = Convert.ToInt32(this.BusinessLogic().PrimaryPerson.TransUnionRequestID);

            if (QuoteServices.ShowSSNPopup(this.BusinessLogic().QHeader.SessionId, pid, st, fc, state, ssn, effd, pt, tu))
            {
                displaySSNModal = true;
                displayedSSN = true;
            }
            else
            {
                displaySSNModal = false;
            }
        }


        public bool ShowForm2Popup()
        {
            //MI Market Value
            decimal RCMVRatio;
            System.Nullable<Int32> marketvalueEnable = 0;
            System.Nullable<decimal> RCMVRatiolimit = 0;
            //calculate RC to MV

            Homesite.IQuote.Data.ISODataProviderTableAdapters.TUScoreRateTableAdapter Marketvalueflow = new Homesite.IQuote.Data.ISODataProviderTableAdapters.TUScoreRateTableAdapter();
            Marketvalueflow.hssp_hs_MarketValue_workflow_enabled(this.BusinessLogic().Header.RatingVersion, this.BusinessLogic().Header.FormNumber, ref marketvalueEnable, ref RCMVRatiolimit);

            if (marketvalueEnable == 1)
            {
                if (this.BusinessLogic().Coverage.EstimatedReplCostAmt != 0 && this.BusinessLogic().Coverage.EstimatedReplCostAmt != null &&
                    this.BusinessLogic().Structure.ValueOfHome != null && this.BusinessLogic().Structure.ValueOfHome != 0)
                {
                    RCMVRatio = Convert.ToDecimal(this.BusinessLogic().Coverage.EstimatedReplCostAmt) / Convert.ToDecimal(this.BusinessLogic().Structure.ValueOfHome);

                    if (RCMVRatio >= RCMVRatiolimit)
                    {
                        return true;
                    }
                }
            }

            return false;
        }


        public AjaxResponse SavePurchaseInfo(QuoteHeader qHeader, PurchaseData pData)
        {
            #region Legacy Business Logic Ported code
            this.PData = pData;
            this.PurchaseProcessor.SavePhoneNumber();
            this.PurchaseProcessor.SaveMailingAddress();
            this.PurchaseProcessor.SaveMortgageInfo();
            this.PurchaseProcessor.SaveConsentToRate(out this.ErrorMessage);
            this.PurchaseProcessor.SavexWind(out this.ErrorMessage); // ITR#7684
            this.PurchaseProcessor.SaveConsentToRateAndxWind(out this.ErrorMessage); // ITR#7684
            this.PurchaseProcessor.SavePaymentDetails();
            this.DataBinder.SaveDataObjects();
            #endregion

            this.Log("SavePurchaseInfo - before saving Quote... Inspection Result value = " + this.BusinessLogic().Quote.InspectionResult.ToString());
            return this.SaveQuote();
        }

        public AjaxResponse ReCalculate(QuoteHeader qHeader, QuoteData qData)
        {
            this.QHeader = qHeader;
            this.QData = qData;

            // This is to fix an issue where walmart validates email and user changes it.
            if (this.QHeader.PartnerId.Equals(2319))
            {
                QuoteServices quoteServices = new QuoteServices();
                if (!quoteServices.ValidateEmailVerificationCode(this.QHeader.QuoteNumber, QData.cvgPartnerDiscount))
                {

                    if (QData.cvgPartnerDiscount != null)
                    {
                        this.QHeader.OLSStatus = OlsStatusPaperlessStoredId;
                        this.QData.cvgPartnerDiscount = String.Empty;
                    }
                    else
                    {
                        this.QHeader.OLSStatus = OlsStatusPaperlessStoredIdNegative;
                    }
                }
            }

            switch (QHeader.FormCode)
            {
                case 3:
                    if (QData.cvgDeductible != null)
                    {
                        UpdateWindHailOrHurricaneValues(QData.cvgDeductible);
                    }
                    break;
                case 4:
                    if (QData.propertyCoverageDeductible != null)
                    {
                        UpdateWindHailOrHurricaneValues(QData.propertyCoverageDeductible);
                    }
                    break;
            }
            

            this.DataBinder.BindCoverageData();
            
            this.BusinessLogic().Quote.ApexBillingOption = this.QHeader.ApexBillingOption;
            this.BusinessLogic().Quote.Save();
            // do rating
            this.ServiceInterface.CallRatingService();

            //call logging ITR#5549
            LogRateChangeEvent(2);

            DoCompleteQuoteSave();

            // return response
            AjaxResponse response = new AjaxResponse();
            
            //ITR#8181 Apex
            if (qHeader.IsApexState)
            {
                response.WindoidContentList = QuoteServices.GetQuoteSummary(this.BusinessLogic().Header, this.BusinessLogic().Quote, this.BusinessLogic().Coverage);
                response.WindoidContent = QuoteServices.GetQuoteSummaryFromList(response.WindoidContentList, quote.ApexBillingOption);
            }
            else
            {
                response.WindoidContent = QuoteServices.GetQuoteSummary(this.BusinessLogic().Header, this.BusinessLogic().Quote, this.BusinessLogic().Coverage, this.BusinessLogic().Structure.PropertyType, this.BusinessLogic().QHeader.H04Ui_V2);
            }
            response.quoteHeader = this.BusinessLogic().QHeader;
            response.NextPanel = AjaxPanel.Coverage;

            return response;
        }

        private void DoCompleteQuoteSave()
        {
            // We can't save Asynch in the fee states b/c we need horizon to calc state fees.
            //if (QuoteServices.IsFeeState(QHeader.State))
            //{
            //    ServiceInterface.CallSaveQuoteService();

            //}
            //else
            //{
            //    ServiceInterface.CallSaveQuoteServiceAsync();
            //}
            //Added by Venkat as part of NJ-Apex changes
            this.ServiceInterface.CallSaveQuoteServiceAsync();
        }

        public bool CoveragesIsNext()
        {
            throw new NotImplementedException();
        }

        public void LogSSNDisplay()
        {

        }

        public void LogSSNDisplay(AjaxResponse response)
        {

        }

        public void GetNavigationInfo()
        {
            if (this.BusinessLogic().QHeader.Step == "Purchase" && this.BusinessLogic().QHeader.Direction == "Back")
            {
                this.PreviousAjaxPanel = AjaxPanel.Coverage;
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        #endregion


        private AjaxPanel GetAjaxPanelFromString(string input)
        {
            switch (input)
            {
                case "About_You":
                    return AjaxPanel.About_You;
                case "Additional_Info":
                    return AjaxPanel.Additional_Info;
                case "Property_Info":
                    return AjaxPanel.Property_Info;
                case "iQuote":
                    return AjaxPanel.iQuote;
                case "Coverage":
                    return AjaxPanel.Coverage;
                case "Your_Address":
                    return AjaxPanel.Your_Address;
                default:
                    return AjaxPanel.Error;
            }
        }

        public string GetABTestGroup(string testName)
        {
            return ABTestInterface.GetABTestGroup(testName);
        }

        public void SetABTestGroup(string testName, string testGroup)
        {
            ABTestInterface.SetABTestGroup(testName, testGroup);
        }

        public DNQReferralStatus GetDNQReferralStatus()
        {
            var visitable = this as IBusinessLogicVisitable;
            DNQReferralStatus.DNQReason reason = DNQReferralStatus.DNQReason.None;
            if (string.Compare(visitable.Quote.DNQReasons, "The Property is not owner occupied.", true) == 0)
                reason = DNQReferralStatus.DNQReason.NonOwnerOccupied;
            if (string.Compare(visitable.Quote.DNQReasons, "Mobile and Manufactured homes are ineligible for coverage.", true) == 0)
                reason = DNQReferralStatus.DNQReason.MobileManufactured;

            DNQReferralStatus status = new DNQReferralStatus()
            {
                CanRefer = false,
                ReferredPartnerName = null,
                Reason = reason
            };

            if (reason != DNQReferralStatus.DNQReason.None)
            {
                LookupProvider lookup = new LookupProvider();
                bool? exclude = true;
                string DNQMessage = "";

                // add Partner functionality lookup
                if (lookup.CheckPartnerFunction(lookup.GetPartnerInfoByPartnerID((int)visitable.Quote.PartnerId).ContactPartnerid, "DNQReferral"))
                {
                    // exclusion lookup
                    Homesite.IQuote.Data.LookupDataProviderTableAdapters.QueriesTableAdapter
                        adp = new Homesite.IQuote.Data.LookupDataProviderTableAdapters.QueriesTableAdapter();
                    adp.CheckDNQReferralExclusions(visitable.PropertyAddress.PostalCode, visitable.Quote.DNQReasons, ref exclude, ref DNQMessage);

                    if (((int)visitable.Quote.PartnerId == 1220) && ((bool)!exclude))
                    {
                        XElement partner = ConfigurationCache.StaticCollection.GetDNQReferralPartner("AMIG");
                        var query =
                            from el in partner.Descendants("ElephantStates").Elements()
                            where (string)el.Value.ToString() == (string)visitable.QHeader.State
                            select el.Value;
                        if (query.Count() > 0)
                            exclude = false;
                        else
                            exclude = true;
                    }
                }

                if (exclude == false)
                {
                    string groupName = GetABTestGroup("DNQ Referral");
                    if (groupName != "Do Not Refer")
                    {
                        status.CanRefer = true;
                        status.ReferredPartnerName = groupName;
                    }
                }
            }
            return status;
        }


        public void SaveEligibilityData(int? squareFootage, int? yearBuilt, int? marketValue)
        {
            this.ServiceInterface.SaveEligibilityData(squareFootage, yearBuilt, marketValue);
        }


        public void SetEligibilityFlagValue(int squareFootage, int yearBuilt, int marketValue)
        {
            this.DataBinder.SetEligibilityFlagValue(squareFootage, yearBuilt, marketValue);
        }

        private void LogRateChangeEvent(int changeId)
        {
            LogPublishClient.LogRate(QHeader, this.BusinessLogic().Quote, changeId, (LogPublisherService.RateChangeReasons)(changeId - 1));
        }

        #region ITR#5599 Logging Functions
        private void LogConsumerEvents(decimal? previousPremiumAmt)
        {
            _ratingId++;
            if (_ratingId == 1 || displayedSSN)
            {
                if (displayedSSN)
                {
                    LogRateChangeEvent(1);
                    displayedSSN = false;
                }

                SocialSecurityNumber();

                if (_ratingId == 1 && QHeader.H04Ui_V2 == false && !displaySSNModal)
                {
                    // log initial old ho4 rate
                    LogRateChangeEvent(1);
                }
                if (((previousPremiumAmt == null) || (previousPremiumAmt == 0)) && !displaySSNModal)
                {
                    LogRateChangeEvent(1);
                }
                else if (qHeader.IsRetrieve && (previousPremiumAmt != this.BusinessLogic().Quote.PolicyPremiumAmt))
                {
                    LogRateChangeEvent(7);
                }

                // Log bindable status.
                if (_savingQuote && qHeader.Step != "Coverage")
                    return;

                if ((!QuoteServices.IsFeeState(qHeader.State)) && !displayedSSN && qHeader.CompleteQuoteFlag == 5)
                    LogPublishClient.LogEvent(QHeader, 2);
                else if (!displayedSSN)
                    LogPublishClient.LogEvent(QHeader, 3);
            }
            else if ((_ratingId > 1) && (previousPremiumAmt != this.BusinessLogic().Quote.PolicyPremiumAmt) && !displaySSNModal)
            {
                LogRateChangeEvent(7);
            }
        }

        private void OldFlowPropertyInfoLogging(decimal? premiumAmt)
        {
            if ((premiumAmt == null) || (premiumAmt == 0))
            {
                LogRateChangeEvent(1);
            }
            else if (premiumAmt != this.BusinessLogic().Quote.PolicyPremiumAmt)
            {
                LogRateChangeEvent(7);
            }
        }

        private void OldFlowAdditionalInfoLogging(decimal? premiumAmt)
        {
            if ((QHeader.CompleteQuoteFlag != 1) && (!QuoteServices.IsFeeState(qHeader.State)))
            {
                LogPublishClient.LogEvent(QHeader, 2);
            }
            if (premiumAmt != this.BusinessLogic().Quote.PolicyPremiumAmt)
            {
                LogRateChangeEvent(7);
            }
        }
        #endregion

        public void SaveValidatedAddress(int CommandCode)
        {
            //unimplemented
        }

        private bool UserChangedAddress()
        {
            DataBinder.RefreshDataObjects();

            return QData.propertyAddressLine1.Trim().ToLower() != this.BusinessLogic().PropertyAddress.Addr1.Trim().ToLower() ||
                   QData.propertyAddressLine2.Trim().ToLower() != this.BusinessLogic().PropertyAddress.Addr2.Trim().ToLower() ||
                   QData.propertyCity.Trim().ToLower() != this.BusinessLogic().PropertyAddress.City.Trim().ToLower();
        }


        private void CheckAddressVerification(AjaxResponse response)
        {
            var zipCentroid = ServiceInterface.IsZipCentroidMatch(
                this.BusinessLogic().PropertyAddressBrokerOutput.MatchCode,
                this.BusinessLogic().PropertyAddressBrokerOutput.LocationQualityCode);


            response.NextPanel = zipCentroid ?
                AjaxPanel.Address_NoMatch :
                AjaxPanel.Address_PartialMatch;

            if ((response.NextPanel == AjaxPanel.Address_NoMatch) || (response.NextPanel == AjaxPanel.Address_PartialMatch))
            {
                DataBinder.PopulateQuoteDataAddresses();
            }
        }


        public void UpdateQdataPropertyAddresses()
        {
            DataBinder.RefreshDataObjects();
            qData.userPropertyAddressLine1 = this.BusinessLogic().UserPropertyAddress.Addr1;
            qData.userPropertyAddressLine2 = this.BusinessLogic().UserPropertyAddress.Addr2;
            qData.userPropertyCity = this.BusinessLogic().UserPropertyAddress.City;
            qData.userPropertyState = this.BusinessLogic().UserPropertyAddress.StateProvCd;
            qData.userPropertyZip = this.BusinessLogic().UserPropertyAddress.PostalCode;
            qData.propertyAddressLine1 = this.BusinessLogic().PropertyAddress.Addr1;
            qData.propertyAddressLine2 = this.BusinessLogic().PropertyAddress.Addr2;
            qData.propertyCity = this.BusinessLogic().PropertyAddress.City;
            qData.propertyState = this.BusinessLogic().PropertyAddress.StateProvCd;
            qData.propertyZip = this.BusinessLogic().PropertyAddress.PostalCode;
        }

        public void SetPropertyAddressToUserEntered()
        {

            this.BusinessLogic().PropertyAddress.Addr1 = this.BusinessLogic().UserPropertyAddress.Addr1;
            this.BusinessLogic().PropertyAddress.Addr2 = this.BusinessLogic().UserPropertyAddress.Addr2;
            this.BusinessLogic().PropertyAddress.City = this.BusinessLogic().UserPropertyAddress.City;
            this.BusinessLogic().PropertyAddress.StateProvCd = this.BusinessLogic().UserPropertyAddress.StateProvCd;
            this.BusinessLogic().PropertyAddress.PostalCode = this.BusinessLogic().UserPropertyAddress.PostalCode;
            this.BusinessLogic().PropertyAddress.PostalCodeExt =
                this.BusinessLogic().UserPropertyAddress.PostalCodeExt ?? string.Empty;
            this.BusinessLogic().PropertyAddress.Save();


        }


        public void SetMailingAddressToUserEntered()
        {
            DataBinder.SetMailingAddressToUserEntered();
        }

        public void SetMailingAddressToPropertyAddress()
        {
            DataBinder.SetMailingAddressToPropertyAddress();
        }


        private void CurrentPropertyAddressToBusinessLogic()
        {
            this.Addr1 = this.BusinessLogic().PropertyAddress.Addr1.ToUpper().Trim();
            this.Addr2 = this.BusinessLogic().PropertyAddress.Addr2.ToUpper().Trim();
            this.City = this.BusinessLogic().PropertyAddress.City.ToUpper().Trim();
            this.State = this.BusinessLogic().PropertyAddress.StateProvCd.ToUpper().Trim();
            this.Zip = this.BusinessLogic().PropertyAddress.PostalCode.ToUpper().Trim();
        }

        private void ProcessOLSEnrollmentQuestions(string quoteNumber, string storeId, bool? paperlessFlag)
        {

            System.Data.DataTable result = DirectWebDAC.GetOLSEnrollmentValues(quoteNumber);
            var EmailVerificationCode = GetEmailVerificationCode();
            bool paperlessSelected = paperlessFlag == true;
            bool callRecalc = false;
            IQuoteConfiguration config = IQuoteConfiguration.GetConfigurationInstance();
            string emailTemplateName = config.Setting("DiscountCodeEmailTemplate");
            // If OLSStatus is not in the correct status skip this logic and only update the row if it exists
            if (this.BusinessLogic().QHeader.OLSStatus >= OlsStatusPaperlessStoredId)
            {
                // New Quote insert the row into the table and send an email
                if (result.Rows.Count == 0)
                {
                    //  Need to call InitiateToken Call Here and pass in call below.
                    string token = InitiateOLSToken();

                    if (!token.Equals(String.Empty) && !String.IsNullOrEmpty(quoteNumber))
                    {
                        DirectWebDAC.InsertOLSEnrollmentValues(quoteNumber, storeId, paperlessSelected,
                                                               EmailVerificationCode, this.BusinessLogic().QData.email,
                                                               token);
                    }
                    else if (String.IsNullOrEmpty(quoteNumber))
                    {
                        //Call failed update OLSStatus
                        this.BusinessLogic().QHeader.OLSStatus = OlsStatusPaperlessStoredIdNegative;
                    }

                    QuoteServices olsServices = new QuoteServices();

                    if (!string.IsNullOrEmpty(QData.email) && !olsServices.ValidateOLSEmail(QData.email))
                    {
                        // Insert values into BatchEmailMessageQueue

                        DirectWebDAC.InsertValidationEmail(QHeader.PartnerId, quoteNumber, QData.email, QData.firstName, QData.lastName, EmailVerificationCode, storeId, paperlessSelected, config.PartnerName, emailTemplateName);
                    }
                    else
                    {
                        // User disregarded the error and will not get a discount
                        this.BusinessLogic().QHeader.OLSStatus = OlsStatusEmailAlreadyEnrolled; // OLSStatus for invalid email
                    }
                }
                // Quote exists in the table update the row
                else
                {
                    // Check if the email is a new email, if it is update the email and the email veri
                    if (!result.Rows[0]["emailAddress"].Equals(this.BusinessLogic().QData.email))
                    {
                        // Call to get new token from OLS and pass in call below
                        string token = InitiateOLSToken();

                        if (!token.Equals(String.Empty) && !String.IsNullOrEmpty(quoteNumber))
                        {
                            DirectWebDAC.UpdateOLSEnrollment(quoteNumber, storeId, paperlessSelected,
                                                                 EmailVerificationCode, this.BusinessLogic().QData.email,
                                                                   token);
                            UpdateDefaultWalmartDiscount("ASSOCIATE15");
                        }
                        else if (String.IsNullOrEmpty(quoteNumber))
                        {
                            //Call failed update OLSStatus
                            this.BusinessLogic().QHeader.OLSStatus = OlsStatusPaperlessStoredIdNegative;
                        }



                        QuoteServices olsServices = new QuoteServices();
                        if (!string.IsNullOrEmpty(QData.email) && !olsServices.ValidateOLSEmail(QData.email))
                        {
                            // Insert values into BatchEmailMessageQueue
                            DirectWebDAC.InsertValidationEmail(QHeader.PartnerId, quoteNumber, QData.email, QData.firstName, QData.lastName, EmailVerificationCode, storeId, paperlessSelected, config.PartnerName, emailTemplateName);

                            // Clear discount code and set callRecalc to true.
                            callRecalc = true;
                            //this.BusinessLogic().Quote.AutoPolicyNumber = String.Empty;
                            //this.BusinessLogic().QData.cvgPartnerDiscount = String.Empty;

                            if (qHeader.H04Ui_V2 == false)
                            {
                                // We do not need to call rating again for non-h04V2 states as this is called twice after the about you page.
                                callRecalc = false;
                            }

                            this.BusinessLogic().Quote.Save();
                        }
                        else
                        {
                            // User disregarded the error an will not get a discount
                            this.BusinessLogic().QHeader.OLSStatus = OlsStatusEmailAlreadyEnrolled; // OLSStatus for invalid email
                        }
                    }
                    // update the storeId as all other feilds will be unchanged.
                    else if (!result.Rows[0]["CustomerTrackingId"].Equals(this.BusinessLogic().QData.storeID))
                    {
                        DirectWebDAC.UpdateOLSEnrollment(quoteNumber, storeId, paperlessSelected,
                                                         result.Rows[0]["emailverificationcode"].ToString(),
                                                         this.BusinessLogic().QData.email, result.Rows[0]["Token"].ToString());
                    }
                    else if (!result.Rows[0]["PaperlessFlag"].Equals(this.BusinessLogic().QData.paperless))
                    {
                        DirectWebDAC.UpdateOLSEnrollment(quoteNumber, storeId, paperlessSelected,
                                                         result.Rows[0]["emailverificationcode"].ToString(),
                                                         this.BusinessLogic().QData.email, result.Rows[0]["Token"].ToString());
                        this.BusinessLogic().QHeader.OLSStatus = OlsStatusPaperlessStoredId;
                    }
                }
            }
            // update the row even if the OLSStatus is not 1 to update the storeId and Paperless.
            // Do not allow the email to be updated as this will cause issue if OLSStatus becomes 1.
            else if (result.Rows.Count != 0)
            {
                DirectWebDAC.UpdateOLSEnrollment(quoteNumber, storeId, paperlessSelected,
                                                         result.Rows[0]["emailverificationcode"].ToString(), result.Rows[0]["emailAddress"].ToString(), result.Rows[0]["Token"].ToString());

                // Check to see if the user removed their storeid or unselected paperless.
                if (string.IsNullOrEmpty(storeId) || paperlessSelected == false)
                {
                    if (this.BusinessLogic().QHeader.H04Ui_V2)
                    {
                        callRecalc = true;
                    }

                    this.BusinessLogic().Quote.AutoPolicyNumber = String.Empty;
                    this.BusinessLogic().QData.cvgPartnerDiscount = String.Empty;
                    this.BusinessLogic().Quote.Save();
                }
            }

            // If we make a change that requires a new rating call then call it here.
            if (callRecalc)
            {

                this.ServiceInterface.CallRatingService();
                //Strip out default
                if (this.BusinessLogic().Quote.AutoPolicyNumber.Equals("ASSOCIATE15"))
                {
                    this.BusinessLogic().Quote.AutoPolicyNumber = String.Empty;
                    this.BusinessLogic().QData.cvgPartnerDiscount = String.Empty;
                }

            }

        }

        private void UpdateDefaultWalmartDiscount(string discountCode)
        {
            this.BusinessLogic().Quote.AutoPolicyNumber = discountCode;
            this.BusinessLogic().Quote.Save();
        }

        private string GetEmailVerificationCode()
        {
            const int eMailVerificationCodeLength = 10;
            string eMailVerificationCode = Guid.NewGuid().ToString();
            Random rand = new Random();
            eMailVerificationCode = eMailVerificationCode.Replace("-", "");
            eMailVerificationCode = eMailVerificationCode.Substring(rand.Next(eMailVerificationCode.Length - eMailVerificationCodeLength), eMailVerificationCodeLength);
            return eMailVerificationCode;
        }

        private string InitiateOLSToken()
        {
            string token = String.Empty;
            ValidationService.ValidationServiceClient validationClient = null;
            try
            {
                validationClient = new ValidationServiceClient();
                var request = new ValidationRequestMessage();
                request.Channel = ValidationChannel.ConsumerWeb;
                request.PartnerAccountNumber = this.BusinessLogic().QHeader.PartnerId.ToString();
                request.DeviceAddress = this.BusinessLogic().QData.email;
                request.RequestType = ValidationRequestType.Token;
                ValidationServiceMessage validationService = validationClient.Initiate(request);
                if (validationService.Result)
                {
                    ValidateResponseMessage validationReference = (ValidateResponseMessage)validationService;
                    token = validationReference.RequestId;
                }

            }
            catch (Exception e)
            {
                this.Log("Exception occurred in InitiateOLSToken: Message: " + e.Message);

            }
            finally
            {
                if (validationClient != null)
                {
                    validationClient.Close();
                }
            }
            return token;
        }

        private bool ProcessWalmartFlow()
        {
            IQuoteConfiguration config = IQuoteConfiguration.GetConfigurationInstance();

            var process = config.Setting("ProcessWalmartFlow");
            if (process != null)
            {
                return process.ToLower().Equals("true");
            }

            return false;
        }

        private void SetWalmartSpecificHeaderFields()
        {
            this.BusinessLogic().QHeader.IsStateEligible =
                            Homesite.ECommerce.DAC.DirectWebDAC.GetWalmartCustomerEligilbeState(this.BusinessLogic().QHeader.State, this.BusinessLogic().QHeader.PartnerId, DateTime.Parse(this.BusinessLogic().QHeader.PolicyEffectiveDate), this.BusinessLogic().QHeader.FormCode);

        }

        #region Multi Rate

        /// <summary>
        /// Creates a ControlDataSource object containing the values of the wind/hail/hurricane options. This is used in multirate
        /// process so each deductible can be calculated with the correct default selection.
        /// 
        /// The logic of local variable "ATerritoryOrBCEG" was duplicated from CoveragesDataSourceProvider.cs, which is the origination
        /// of the "GetWindHailDropdown" logic.
        /// </summary>
        /// <param name="quoteHeader">User-value container</param>
        /// <param name="coverage">coverage data value container</param>
        /// <param name="abOutputs">address broker value container</param>
        /// <returns>ControlDataSource - only to be used w/in ProcessMultirate()</returns>
        private ControlDataSource GetWindHailDataSource(QuoteHeader quoteHeader, Coverage coverage, AddressBrokerOutput abOutputs)
        {
            short? ATerritoryOrBCEG = (quoteHeader.State == "GA") && (abOutputs.BuildingCodeEffectivenessGradeCd != null)
                    ? (short?)Convert.ToInt32(abOutputs.BuildingCodeEffectivenessGradeCd)
                    : abOutputs.ATerritory;
            
            DataTable dtWindHail = DirectWebDAC.GetWindHailDropdown(
                quoteHeader.State,
                quoteHeader.ProgramType,
                quoteHeader.FormCode,
                abOutputs.WindPool,
                quoteHeader.RatingVersion,
                (int) abOutputs.ShoreLineDistance,
                ATerritoryOrBCEG,
                Convert.ToInt32(abOutputs.RatingTerritory),
                coverage.PolicyEffDate,
                string.IsNullOrEmpty(quoteHeader.OriginalQuoteDate) == false ? DateTime.Parse(quoteHeader.OriginalQuoteDate) : DateTime.Now,
                abOutputs.LandSlide,
                null,
                (decimal) coverage.CovAAmount, Convert.ToInt32(abOutputs.GridId ?? 0));

            if (dtWindHail != null)
            {
                return new ControlDataSource(dtWindHail, "Deductible", "Deductible");
            }

            return null;
        }

        /// <summary>
        /// Following Google protocol, generates a digital signature for the Url provided and returns it. This
        /// algorithm was taken directly from Google.
        /// </summary>
        /// <param name="url">The portion of the Url to generate a signature for.</param>
        /// <see cref="https://code.google.com/p/gmaps-samples/source/browse/trunk/urlsigning/UrlSigner.cs?spec=svn2487&r=2487"/>
        /// <returns>A signed version of the given Url.</returns>
        public string SignGoogleUrl(string url, string key)
        {
            // The below code is verbatim from Google, as it should be.
            ASCIIEncoding encoding = new ASCIIEncoding();

            // converting key to bytes will throw an exception, need to replace '-' and '_' characters first.
            string usablePrivateKey = key.Replace("-", "+").Replace("_", "/");

            byte[] privateKeyBytes = Convert.FromBase64String(usablePrivateKey);

            Uri uri = new Uri(url);
            byte[] encodedPathAndQueryBytes = encoding.GetBytes(uri.LocalPath + uri.Query);

            // compute the hash
            HMACSHA1 algorithm = new HMACSHA1(privateKeyBytes);

            byte[] hash = algorithm.ComputeHash(encodedPathAndQueryBytes);

            // convert the bytes to string and make url-safe by replacing '+' and '/' characters
            string signature = Convert.ToBase64String(hash).Replace("+", "-").Replace("/", "_");

            // Add the signature to the existing URI.
            return uri.Scheme + "://" + uri.Host + uri.LocalPath + uri.Query + "&signature=" + signature;
        }

        #endregion

    }
}
