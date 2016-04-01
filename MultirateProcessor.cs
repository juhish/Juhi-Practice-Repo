using System;
using System.Data;
using System.Linq;
using Homesite.ECommerce.Context;
using Homesite.ECommerce.DAC;
using Homesite.ECommerce.IQuote;
using Homesite.ECommerce.ServiceLibrary.Coverages;
using Homesite.IQuote.Data;
using Homesite.IQuote.LookupBroker;

namespace Homesite.ECommerce.ServiceLibrary.BusinessLogic
{
    /// <summary>Shared MultiRate proccessor for all BusinessLogic's to utilize</summary>
    public class BusinessLogicMultirateProcessor : IBusinessLogicVisitor
    {
        public delegate void LogMethod(string msg);
        private void LogFallback(string msg) { }

        /// <summary>Allows the BusinessLogic to determine how debug messages are logged</summary>
        LogMethod _onLog;

        /// <summary>This visitor requires the use of ServiceInterface visitor, and is a required argument during construction</summary>
        BusinessLogicServiceInterface _serviceInterface;

        /// <summary>Due to the difference in configuration retrieval in ConsumerWeb and RWD, we must specify which method to use in retrieval</summary>
        bool _useConfigManagement;

        /// <summary>This IBusinessLogicVisitor requires a reference to the ServiceInterface Visitor,
        /// instead of making it a public variable to all visitors, it is a required argument during construction</summary>
        /// <param name="serviceInterface">BusinessLogicServiceInterface</param>
        /// <param name="useConfigManagement">Depending if we're in </param>
        /// <param name="onLog">Argue a lambda which logs a debug message to your choosing</param>
        public BusinessLogicMultirateProcessor(BusinessLogicServiceInterface serviceInterface, bool useConfigManagement, LogMethod onLog = null)
            : base()
        {
            _serviceInterface = serviceInterface;
            _useConfigManagement = useConfigManagement;
            _onLog = onLog != null ? onLog : LogFallback;
        }

        /// <summary>Initialize BusinessLogic's QuoteHeader.IsMultiRate flag for the first time, calling to DirectWebDAC</summary>
        public void InitializeEnabled()
        {
            IBusinessLogicVisitable bl = this.BusinessLogic();
            if (bl.Header.PartnerID.HasValue && bl.Header.FormNumber.HasValue && string.IsNullOrEmpty(bl.Header.State) == false)
            {
                bool isretrieve = bl.Header.isRetrieve.HasValue && bl.Header.isRetrieve.Value;
                bl.QHeader.IsMultiRate = isretrieve ? false : DirectWebDAC.GetIsMultiRateEnabled(bl.Header.PartnerID.Value, bl.Header.FormNumber.Value, bl.QHeader.State);
            }
            else
            {
                bl.QHeader.IsMultiRate = false;
            }
        }

        /// <summary>Determine if MultiRate should be enabled</summary>
        /// <returns>'true' = multirate is enabled, 'false' = multirate is disabled</returns>
        public bool IsEnabled()
        {
            return this.BusinessLogic().QHeader.IsMultiRate;
        }

        /// <summary>Since the BusinessLogic is responsible for assigning their coverages, this public method
        /// retreives the multirateoptionset for a given key</summary>
        /// <param name="selectionKey">key of the multirateoption set, in RWD its 0,1,2</param>
        /// <returns>populated MultiRateOptionSet object</returns>
        public MultiRateOptionSet GetOptionSetByKey(int selectionIndex)
        {
            IBusinessLogicVisitable bl = this.BusinessLogic();
            MultiRateOptionsForm3 mr_options = GetMultiRateOptions() as MultiRateOptionsForm3;
            return mr_options.GetOptionsAtIndex(selectionIndex);
        }

        /// <summary>
        /// if this state has 1 or 0 options, reset Cov-A percentages to that
        /// </summary>
        /// <param name="option">optionally argue what the value from multirate config was, otherwise, just give the value from data provider</param>
        private string GetExtendedReplacementCost(double output)
        {
            ControlDataSource windHailOptions = GetWindHailDataSource();
            IControlDataSource cds = GetExtendedReplacementCost();

            if ((cds.Items.Count == 1) && (cds.Items.ElementAt(0).Value.Trim() == "25%"))
            {
                output = 0.25;
            }
            else if ((cds.Items.Count == 1) && (cds.Items.ElementAt(0).Value.Trim() == "50%"))
            {
                output = 0.5;
            }
            else if (((cds.Items.Count == 1) && (cds.Items.ElementAt(0).Value.Trim() == "None")) || (cds.Items.Count == 0))
            {
                output = 0;
            }

            return output.ToString();
        }

        /// <summary>
        /// Check if we have only 1 extended replacement option, and reset multirate values to that value if so
        /// </summary>
        /// <param name="option">form 3 multirate options to be updated</param>
        private void SetExtendedReplacementToOption(ref MultiRateOptionsForm3 option)
        {
            ControlDataSource windHailOptions = GetWindHailDataSource();
            IControlDataSource cds = GetExtendedReplacementCost();

            if ((cds.Items.Count == 1) && (cds.Items.ElementAt(0).Value.Trim() == "25%"))
            {
                option.CoverageAPercent[0] = 0.25;
                option.CoverageAPercent[1] = 0.25;
                option.CoverageAPercent[2] = 0.25;
            }
            else if ((cds.Items.Count == 1) && (cds.Items.ElementAt(0).Value.Trim() == "50%"))
            {
                option.CoverageAPercent[0] = 0.5;
                option.CoverageAPercent[1] = 0.5;
                option.CoverageAPercent[2] = 0.5;
            }
            else if (((cds.Items.Count == 1) && (cds.Items.ElementAt(0).Value.Trim() == "None")) || (cds.Items.Count == 0))
            {
                option.CoverageAPercent[0] = 0;
                option.CoverageAPercent[1] = 0;
                option.CoverageAPercent[2] = 0;
            }
        }

        /// <summary>Main Process method called when moving to the coverage page</summary>
        public void ProcessMultiRateForm3()
        {
            DateTime start = DateTime.Now;
#if DEBUG
            _onLog("MultirateProcessor.ProcessMultiRateForm3() - Begin");
#endif

            IBusinessLogicVisitable bLogic = this.BusinessLogic();
            MultiRateOptionsForm3 mrOptions = GetMultiRateOptions() as MultiRateOptionsForm3;

            decimal? currentCovCAmount = bLogic.Coverage.CovCAmount;
            decimal? currentCovAAmount = bLogic.Coverage.CovAAmount;
            string currentRatingCaseNumber = bLogic.Quote.RatingCaseNumber.Trim();
            string currentDeductible = bLogic.Coverage.Deductible.Trim();

            ControlDataSource windHailOptions = GetWindHailDataSource();
            IControlDataSource cds = GetExtendedReplacementCost();

            SetExtendedReplacementToOption(ref mrOptions);

            for (int i = 0; i < 3; i++)
            {
                MultiRateOptionSetForm3 mr_optionSet = mrOptions.GetOptionsAtIndex(i) as MultiRateOptionSetForm3;

                string newDeductible = mr_optionSet.Deductible;

                string windHailSelection = string.Empty;
                if (windHailOptions != null)
                {
                    windHailSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH03(
                        windHailOptions,
                        newDeductible,
                        bLogic.Coverage.CovAAmount.ToString(),
                        bLogic.Coverage.HurricaneDeductible).Trim();

                    if (windHailSelection.Contains("%"))
                    {
                        windHailSelection = windHailSelection.Trim().Length > 0 ? windHailSelection : string.Empty;
                    }
                    else if (windHailSelection.ToUpper().Trim().Contains("NO COVERAGE"))
                    {
                        windHailSelection = string.Empty;
                    }
                    else
                    {
                        decimal? windHailOrHurricaneDec = windHailSelection.Trim().Length > 0 ? Convert.ToDecimal(windHailSelection.Replace("$", "")) : 0;
                        windHailSelection = windHailSelection.Trim();
                        decimal? newDeductibleDec = newDeductible.Trim().Length > 0 ? Convert.ToDecimal(newDeductible.Replace("$", "")) : 0;
                        if (!(windHailOrHurricaneDec >= newDeductibleDec))
                        {
                            windHailSelection = string.Empty;
                        }
                    }
                }

                mrOptions.CoverageCPercent[i] = GetIncreasedCoverageCValues(bLogic.Coverage.CovAAmount, bLogic.Coverage.CovCAmount, mr_optionSet.CoverageC);

                mrOptions.CoverageAPercent[i] = (double)GetIncreasedCoverageAValues(bLogic.Coverage.CovAAmount, mr_optionSet.CoverageA);

                decimal? newCovEAmount = mr_optionSet.CoverageE;
                decimal? newCovFAmount = mr_optionSet.CoverageF;
                decimal? newCovCAmount = (decimal?)mrOptions.CoverageCPercent[i];
                decimal? newCovCAmountForUi = currentCovCAmount + (decimal?)mrOptions.CoverageCPercent[i];
                int coveredPeril = mr_optionSet.CoveragePerils;
                decimal? newCovAAmount = (decimal)(mr_optionSet.CoverageA * 100);
                decimal? newCovAAmountForUi = currentCovAAmount;
                decimal? newIncreasedCovAAmountForUi = (decimal?)mrOptions.CoverageAPercent[i];

                if (_serviceInterface.HasDangerousDogCoverage())
                {
                    newCovEAmount = bLogic.Coverage.CovEAmount;
                    newCovFAmount = bLogic.Coverage.CovFAmount;
                }
                if (HomeDayCare.CoverageFlag.GetValueOrDefault())
                {
                    _serviceInterface.CallHomeDayCareCoverageDefaults();
                    newCovEAmount = bLogic.Coverage.CovEAmount;
                }

#if DEBUG
                _onLog(string.Format("Processing Multirate ReRate - Session:{0}, Deductible:{1}, WindHailSelection:{2}, CoverageE:{3}, CoverageF:{4}, CoverageC:{5}",
                    bLogic.Header.SessionID, currentDeductible, windHailSelection, newCovEAmount, newCovFAmount, newCovCAmount));
#endif

                string ratedPremium;
                CallReRate(
                    currentDeductible,
                    currentRatingCaseNumber,
                    newDeductible,
                    windHailSelection,
                    newCovEAmount,
                    newCovFAmount,
                    newCovCAmount,
                    newCovAAmount,
                    coveredPeril,
                    bLogic.QHeader.FormCode,
                    out ratedPremium,
                    out currentDeductible,
                    out currentRatingCaseNumber);

#if DEBUG
                _onLog(string.Format("ReRate premium result: {0}", ratedPremium));
#endif

                DirectWebDAC.InsertOrUpdateMultirate(
                    bLogic.Quote.SessionId,
                    newDeductible,
                    0,
                    currentRatingCaseNumber,
                    ratedPremium,
                    windHailSelection,
                    string.Empty,
                    newCovEAmount,
                    newCovFAmount,
                    "Option" + (i + 1),
                    newCovCAmountForUi,
                    newCovAAmountForUi,
                    coveredPeril, 
                    string.Empty, 0, 0, 0, 
                    newIncreasedCovAAmountForUi);
            }

#if DEBUG
            _onLog(string.Format("MultirateProcessor.ProcessMultiRateForm3() - Complete - ProcessTime:{0}ms", (DateTime.Now - start).Milliseconds));
#endif
        }

        /// <summary>Main Process method called when moving to the coverage page</summary>
        public void ProcessMultiRateForm4()
        {
            DateTime start = DateTime.Now;
#if DEBUG
            _onLog("MultirateProcessor.ProcessMultiRateForm4() - Begin");
#endif

            IBusinessLogicVisitable bLogic = this.BusinessLogic();

            string currentRatingCaseNumber = bLogic.Quote.RatingCaseNumber.Trim();
            string currentDeductible = bLogic.Coverage.Deductible.Trim();

            MultiRateOptionsForm4 options = GetMultiRateOptions() as MultiRateOptionsForm4;

            if (options != null && options.IsValid(3) == false)
            {
                return;
            }

            for (int i = 0; i < 3; i++)
            {
                var multiRateOption = options.GetOptionsAtIndex(i) as MultiRateOptionSetForm4;

                string newDeductible = multiRateOption.Deductible;

                string hurricaneSelection;
                bool hasHurricaneOrWindHail;
                string windHailSelection;
                GetWindHailOrHurricaneValues(newDeductible, out windHailSelection, out hurricaneSelection, out hasHurricaneOrWindHail);

                if (!hasHurricaneOrWindHail)
                {
                    hurricaneSelection = string.Empty;
                }

                decimal? covE = multiRateOption.CoverageE;
                decimal? covF = multiRateOption.CoverageF;
                decimal? covCAmount = bLogic.Coverage.CovCAmount;
                decimal? covAAmount = 0;
                decimal? covAAmountForUi = 0;

#if DEBUG
                _onLog(string.Format("Processing Multirate ReRate - Session:{0}, RatingCaseNumber: {1}, Deductible:{2}, WindHailSelection:{3}, CoverageE:{4}, CoverageF:{5}, CoverageC:{6}, CoverageA:{7}",
                    bLogic.Header.SessionID, currentRatingCaseNumber, currentDeductible, hurricaneSelection, covE, covF, covCAmount, covAAmount));
#endif

                string ratedPremium;
                CallReRate(
                    currentDeductible,
                    currentRatingCaseNumber,
                    newDeductible,
                    hurricaneSelection,
                    covE,
                    covF,
                    covCAmount,
                    covAAmount,
                    0,
                    bLogic.QHeader.FormCode,
                    out ratedPremium,
                    out currentDeductible,
                    out currentRatingCaseNumber);

#if DEBUG
                _onLog(string.Format("ReRate result: {0}", ratedPremium));
#endif

                DirectWebDAC.InsertOrUpdateMultirate(
                    bLogic.Quote.SessionId,
                    newDeductible,
                    0,
                    currentRatingCaseNumber,
                    ratedPremium,
                    hurricaneSelection,
                    string.Empty,
                    covE,
                    covF,
                    "Option" + (i + 1),
                    covCAmount,
                    covAAmountForUi,
                    0, string.Empty, 0, 0, 0, 0);
            }

#if DEBUG
            _onLog(string.Format("MultirateProcessor.ProcessMultiRateForm4() - Complete - ProcessTime:{0}ms", (DateTime.Now - start).Milliseconds));
#endif
        }

        #region Statics

        /// <summary>
        /// Updates the argued QuoteSummary object with MultiRate data.
        /// </summary>
        /// <param name="header">BusinessLogic.Header</param>
        /// <param name="quote">BusinessLogic.Quote</param>
        /// <param name="coverage">BusinessLogic.Coverage</param>
        /// <param name="qSummary">reference of the QuoteSummary object to update</param>
        private static void BindMultiRateForForm3(Header header, Quote quote, Coverage coverage, ref QuoteSummary qSummary)
        {
            DataTable table = DirectWebDAC.GetMultirate(header.SessionID);

            for (var i = 0; i < table.Rows.Count; i++)
            {
                string totalPremium = table.Rows[i]["PolicyPremiumAmt"].ToString().Trim().Replace("$", "");
                string deductible = table.Rows[i]["Deductible"].ToString().Trim();
                string windHailOrHurricane = (table.Rows[i]["WindHailOrHurricane"] == null) ? string.Empty : table.Rows[i]["WindHailOrHurricane"].ToString().Trim();
                decimal? covEAmount = (table.Rows[i]["CovEAmount"] == null) ? 0 : (decimal?)table.Rows[i]["CovEAmount"];
                decimal? covFAmount = (table.Rows[i]["CovFAmount"] == null) ? 0 : (decimal?)table.Rows[i]["CovFAmount"];
                decimal? covCAmount = (table.Rows[i]["CovCAmount"] == null) ? 0 : (decimal?)table.Rows[i]["CovCAmount"];
                decimal? covAAmount = (table.Rows[i]["CovAAmount"] == null) ? 0 : (decimal?)table.Rows[i]["CovAAmount"];
                decimal? increasedCovAAmount = (table.Rows[i]["IncreasedCovAAmount"] == null) ? 0 : (decimal?)table.Rows[i]["IncreasedCovAAmount"];
                int coveredPerils = (table.Rows[i]["coveredPerils"] == null) ? 0 : (int)table.Rows[i]["coveredPerils"];
                string multirateOptions = table.Rows[i]["MultirateOptions"].ToString().Trim();

                DateTime ratingDate = DirectWebDAC.GetRatingDate(DateTime.Parse(coverage.PolicyEffDate), 
                                                                quote.InitialQuoteRequestDt.HasValue ? quote.InitialQuoteRequestDt.Value : DateTime.Now,
                                                                header.State);

                DataSet premiumDisplay = DirectWebDAC.GetPremiumDisplay(
                    Convert.ToInt32(table.Rows[i]["RatingCaseNumber"]), 
                    header.PartnerID ?? -1,
                    header.State ?? "", 
                    ratingDate, 
                    (int)header.UnderwritingNumber);

                var premiumDiscounts = (ISODataProvider.GetPremiumDiscountsDataTable)premiumDisplay.Tables["GetPremiumDiscounts"];

                var discounts = new ControlDataSource(premiumDiscounts, "display", "premium", "Discounts");

                double totalDiscount = 0;
                foreach (var discount in discounts.Items)
                {
                    if (discount.Value == "" || discount.Key == "Total Discounts and Surcharges" || discount.Key == "ACV Roof Loss Settlement (Wind/Hail) for roofs older than 10 years")
                    {
                        continue;
                    }

                    double val = Convert.ToDouble(discount.Value);
                    if (val < 0)
                    {
                        totalDiscount = totalDiscount - val;
                    }
                }

                DirectWebDAC.InsertOrUpdateMultirate(header.SessionID, deductible, (Convert.ToInt32(table.Rows[i]["IsDeductibleSelected"]) == 1) ? 1 : 0, table.Rows[i]["RatingCaseNumber"].ToString().Trim(), totalPremium, windHailOrHurricane, totalDiscount.ToString(), covEAmount, covFAmount, multirateOptions, covCAmount, covAAmount, Convert.ToInt32(coveredPerils), string.Empty, 0, 0, 0, increasedCovAAmount);

                switch (multirateOptions)
                {
                    case "Option1":
                        qSummary.TotalPremium1 = totalPremium;
                        qSummary.Deductible1 = deductible;
                        qSummary.TotalDiscount1 = totalDiscount.ToString("f2");
                        qSummary.CovEAmount1 = covEAmount.ToString();
                        qSummary.CovFAmount1 = covFAmount.ToString();
                        qSummary.CovCAmount1 = covCAmount.ToString();
                        qSummary.CovAAmount1 = covAAmount.ToString();
                        qSummary.IncreasedCovAAmount1 = increasedCovAAmount.ToString();
                        qSummary.MultirateOption1 = multirateOptions;
                        qSummary.coveredPerils1 = (coveredPerils == 0) ? "standard" : "extended";
                        break;

                    case "Option2":
                        qSummary.TotalPremium2 = totalPremium;
                        qSummary.Deductible2 = deductible;
                        qSummary.TotalDiscount2 = totalDiscount.ToString("f2");
                        qSummary.CovEAmount2 = covEAmount.ToString();
                        qSummary.CovFAmount2 = covFAmount.ToString();
                        qSummary.CovCAmount2 = covCAmount.ToString();
                        qSummary.CovAAmount2 = covAAmount.ToString();
                        qSummary.IncreasedCovAAmount2 = increasedCovAAmount.ToString();
                        qSummary.MultirateOption2 = multirateOptions;
                        qSummary.coveredPerils2 = (coveredPerils == 0) ? "standard" : "extended";
                        break;

                    case "Option3":
                        qSummary.TotalPremium3 = totalPremium;
                        qSummary.Deductible3 = deductible;
                        qSummary.TotalDiscount3 = totalDiscount.ToString("f2");
                        qSummary.CovEAmount3 = covEAmount.ToString();
                        qSummary.CovFAmount3 = covFAmount.ToString();
                        qSummary.CovCAmount3 = covCAmount.ToString();
                        qSummary.CovAAmount3 = covAAmount.ToString();
                        qSummary.IncreasedCovAAmount3 = increasedCovAAmount.ToString();
                        qSummary.MultirateOption3 = multirateOptions;
                        qSummary.coveredPerils3 = (coveredPerils == 0) ? "standard" : "extended";
                        break;
                }
            }

            qSummary.CovAAmount = coverage.CovAAmount;
            qSummary.MedicalPaymentLimit = coverage.MedicalPaymentLimit;
            qSummary.PersonalLiabilityLimit = coverage.PersonalLiabilityLimit;
        }

        /// <summary>
        /// Updates the argued QuoteSummary object with MultiRate data. This is only used in ConsumerWeb - HO4
        /// </summary>
        /// <param name="header">BusinessLogic.Header</param>
        /// <param name="quote">BusinessLogic.Quote</param>
        /// <param name="coverage">BusinessLogic.Coverage</param>
        /// <param name="qSummary">reference of the QuoteSummary object to update</param>
        private static void BindMultiRateForForm4(Header header, Quote quote, Coverage coverage, ref QuoteSummary qSummary)
        {
            DataTable table = DirectWebDAC.GetMultirate(header.SessionID);

            for (var i = 0; i < table.Rows.Count; i++)
            {
                var totalPremium = table.Rows[i]["PolicyPremiumAmt"].ToString().Trim().Replace("$", "");
                var deductible = table.Rows[i]["Deductible"].ToString().Trim();
                var covCAmount = (table.Rows[i]["CovCAmount"] == null) ? 0 : (decimal?)table.Rows[i]["CovCAmount"];
                var covEAmount = (table.Rows[i]["CovEAmount"] == null) ? 0 : (decimal?)table.Rows[i]["CovEAmount"];
                var covFAmount = (table.Rows[i]["CovFAmount"] == null) ? 0 : (decimal?)table.Rows[i]["CovFAmount"];
                var multirateOptions = table.Rows[i]["MultirateOptions"].ToString().Trim();

                DateTime ratingDate = DirectWebDAC.GetRatingDate(DateTime.Parse(coverage.PolicyEffDate),
                                                                quote.InitialQuoteRequestDt.HasValue ? quote.InitialQuoteRequestDt.Value : DateTime.Now,
                                                                header.State);

                var premiumDisplay = DirectWebDAC.GetPremiumDisplay(
                    Convert.ToInt32(table.Rows[i]["RatingCaseNumber"].ToString()), header.PartnerID ?? -1,
                    header.State ?? "", ratingDate, (int)header.UnderwritingNumber);

                var premiumDiscounts = (ISODataProvider.GetPremiumDiscountsDataTable)premiumDisplay.Tables["GetPremiumDiscounts"];

                var discounts = new ControlDataSource(premiumDiscounts, "display", "premium", "Discounts");

                double totalDiscount = 0;
                foreach (var discount in discounts.Items)
                {
                    if (discount.Value == "" || discount.Key == "Total Discounts and Surcharges" || discount.Key == "ACV Roof Loss Settlement (Wind/Hail) for roofs older than 10 years")
                    {
                        continue;
                    }

                    double val = Convert.ToDouble(discount.Value);
                    if (val < 0)
                    {
                        totalDiscount = totalDiscount - val;
                    }
                }

                var EftStateFee = header.State == "AL" ? "$2.60" : header.State == "KY" ? "$2.50" : header.State == "NC" ? "$0.00" : header.State == "TN" ? "$0.00" : header.State.Equals("FL") ? "$1.00" : "$3.00";

                string totalPrice = (Convert.ToDouble(totalPremium) + totalDiscount).ToString();

                string monthlyPaymentPlan;
                string downPayment;
                int numInstallments;

                Structure structure = Structure.GetStructure(header.SessionID);

                int propertyType = 0;

                if (!String.IsNullOrEmpty(structure.PropertyType))
                {
                    propertyType = Convert.ToInt32(structure.PropertyType);
                }

                QuoteServices.ProcessMonthlyPremiumData(header.State, quote.CompanysQuoteNumber, ((DateTime)ratingDate).ToString("MM/dd/yyyy"), totalPremium, propertyType,
                    out monthlyPaymentPlan, out downPayment, out numInstallments);

                DirectWebDAC.InsertOrUpdateMultirate(header.SessionID, deductible, (Convert.ToInt32(table.Rows[i]["IsDeductibleSelected"]) == 1) ? 1 : 0, table.Rows[i]["RatingCaseNumber"].ToString().Trim(), totalPremium, string.Empty, totalDiscount.ToString(), covEAmount, covFAmount, multirateOptions, 0, 0, 0, totalPrice, Convert.ToDecimal(monthlyPaymentPlan.Trim().Replace("$", "")), Convert.ToDecimal(downPayment.Trim().Replace("$", "")), numInstallments, 0);

                switch (multirateOptions)
                {
                    case "Option1":
                        qSummary.TotalPremium1 = totalPremium;
                        qSummary.Deductible1 = deductible;
                        qSummary.TotalDiscount1 = totalDiscount.ToString("f2");
                        qSummary.CovCAmount1 = covCAmount.ToString();
                        qSummary.CovEAmount1 = covEAmount.ToString();
                        qSummary.CovFAmount1 = covFAmount.ToString();
                        qSummary.MultirateOption1 = multirateOptions;
                        qSummary.TotalPrice1 = totalPrice;
                        qSummary.MonthlyPayPlan1 = monthlyPaymentPlan;
                        qSummary.Downpayment1 = downPayment;
                        qSummary.NumInstallments1 = numInstallments;

                        break;

                    case "Option2":
                        qSummary.TotalPremium2 = totalPremium;
                        qSummary.Deductible2 = deductible;
                        qSummary.TotalDiscount2 = totalDiscount.ToString("f2");
                        qSummary.CovCAmount2 = covCAmount.ToString();
                        qSummary.CovEAmount2 = covEAmount.ToString();
                        qSummary.CovFAmount2 = covFAmount.ToString();
                        qSummary.MultirateOption2 = multirateOptions;
                        qSummary.TotalPrice2 = totalPrice;
                        qSummary.MonthlyPayPlan2 = monthlyPaymentPlan;
                        qSummary.Downpayment2 = downPayment;
                        qSummary.NumInstallments2 = numInstallments;
                        break;

                    case "Option3":
                        qSummary.TotalPremium3 = totalPremium;
                        qSummary.Deductible3 = deductible;
                        qSummary.TotalDiscount3 = totalDiscount.ToString("f2");
                        qSummary.CovCAmount3 = covCAmount.ToString();
                        qSummary.CovEAmount3 = covEAmount.ToString();
                        qSummary.CovFAmount3 = covFAmount.ToString();
                        qSummary.MultirateOption3 = multirateOptions;
                        qSummary.TotalPrice3 = totalPrice;
                        qSummary.MonthlyPayPlan3 = monthlyPaymentPlan;
                        qSummary.Downpayment3 = downPayment;
                        qSummary.NumInstallments3 = numInstallments;
                        break;
                }
            }

            qSummary.MedicalPaymentLimit = coverage.MedicalPaymentLimit;
            qSummary.PersonalLiabilityLimit = coverage.PersonalLiabilityLimit;
        }

        /// <summary>
        /// Updates the argued QuoteSummary object with MultiRate data. This is only used in ConsumerWeb.
        /// </summary>
        /// <param name="header">BusinessLogic.Header</param>
        /// <param name="quote">BusinessLogic.Quote</param>
        /// <param name="coverage">BusinessLogic.Coverage</param>
        /// <param name="qSummary">reference of the QuoteSummary object to update</param>
        public static void UpdateMultiRateToQuoteSummary(Header header, Quote quote, Coverage coverage, ref QuoteSummary qSummary)
        {
            switch (header.FormNumber)
            {
                case 3:
                    BindMultiRateForForm3(header, quote, coverage, ref qSummary);
                    break;
                case 4:
                    BindMultiRateForForm4(header, quote, coverage, ref qSummary);
                    break;
            }
        }

        #endregion Statics

        #region Privates

        /// <summary>Wrapper method to get the MultiRate options from partner configurations</summary>
        /// <returns>IMultiRateOptions</returns>
        private IMultiRateOptions GetMultiRateOptions()
        {
            IBusinessLogicVisitable bl = this.BusinessLogic();

            IMultiRateOptions output = null;

            if (_useConfigManagement)
            {
                output = (new UIServices()).GetMultiRateOptionsRWD(bl.QHeader.FormCode, bl.QHeader.State);
            }
            else
            {
                output = (new UIServices()).GetMultiRateOptions(bl.QHeader.FormCode, bl.QHeader.State);
            }

            return output;
        }

        /// <summary> Get the valid windhail or hurricane deductible </summary>
        /// <param name="selectedDeductible">current deductible</param>
        /// <param name="windHailSelection">windhail selection</param>
        /// <param name="hurricaneSelection">hurricane selection</param>
        /// <param name="isUpdated">set to true if windhail/hurricane was updated during this process</param>
        private void GetWindHailOrHurricaneValues(string selectedDeductible, out string windHailSelection, out string hurricaneSelection, out bool isUpdated)
        {
            windHailSelection = string.Empty;
            hurricaneSelection = string.Empty;
            isUpdated = false;

            ControlDataSource windHailOptions = GetWindHailDataSource();
            if (windHailOptions != null && windHailOptions.Items.Count > 0)
            {
                switch (this.QHeader.FormCode)
                {
                    case 3:
                        windHailSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH03(windHailOptions, selectedDeductible, this.BusinessLogic().Coverage.CovAAmount.ToString(), this.BusinessLogic().Coverage.WindstormDeductible).Trim();
                        hurricaneSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH03(windHailOptions, selectedDeductible, this.BusinessLogic().Coverage.CovAAmount.ToString(), this.BusinessLogic().Coverage.HurricaneDeductible).Trim();
                        break;
                    case 4:
                        windHailSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH04(windHailOptions, selectedDeductible, this.BusinessLogic().Coverage.WindstormDeductible).Trim();
                        hurricaneSelection = CoveragesDataSourceProvider.GetDefaultWindDeductibleSelectionForH04(windHailOptions, selectedDeductible, this.BusinessLogic().Coverage.HurricaneDeductible).Trim();
                        break;
                }
                isUpdated = true;
            }

#if DEBUG
            string toLog = string.IsNullOrEmpty(windHailSelection) ? hurricaneSelection : windHailSelection;
            if (string.IsNullOrEmpty(toLog))
            {
                toLog = "WindHail or Hurricane deductible not available";
            }
            _onLog(string.Format("MultirateProcessor.GetWindHailOrHurricaneValues(deductible:{0}) - chosen deductible: {1}", selectedDeductible, toLog));
#endif
        }

        /// <summary>
        /// Creates a ControlDataSource object containing the values of the wind/hail/hurricane options. This is used in multirate
        /// process so each deductible can be calculated with the correct default selection.
        /// 
        /// The logic of local variable "ATerritoryOrBCEG" was duplicated from CoveragesDataSourceProvider.cs, which is the origination
        /// of the "GetWindHailDropdown" logic.
        /// </summary>
        /// <returns>ControlDataSource - only to be used w/in ProcessMultirate</returns>
        public ControlDataSource GetWindHailDataSource()
        {
            ControlDataSource output = null;
            IBusinessLogicVisitable bl = this.BusinessLogic();
            AddressBrokerOutput abOutputs = AddressBrokerOutputs.GetAddressBrokerOutputs(bl.Header.SessionID)[0];

            short? ATerritoryOrBCEG = (bl.QHeader.State == "GA") && (abOutputs.BuildingCodeEffectivenessGradeCd != null)
                    ? (short?)Convert.ToInt32(abOutputs.BuildingCodeEffectivenessGradeCd)
                    : abOutputs.ATerritory;

            DataTable dtWindHail = DirectWebDAC.GetWindHailDropdown(
                bl.QHeader.State,
                bl.QHeader.ProgramType,
                bl.QHeader.FormCode,
                abOutputs.WindPool,
                bl.QHeader.RatingVersion,
                (int)abOutputs.ShoreLineDistance,
                ATerritoryOrBCEG,
                Convert.ToInt32(abOutputs.RatingTerritory),
                bl.Coverage.PolicyEffDate,
                string.IsNullOrEmpty(bl.QHeader.OriginalQuoteDate) == false ? DateTime.Parse(bl.QHeader.OriginalQuoteDate) : DateTime.Now,
                abOutputs.LandSlide,
                null,
                (decimal)bl.Coverage.CovAAmount, Convert.ToInt32(abOutputs.GridId ?? 0));

            if (dtWindHail != null)
            {
                output = new ControlDataSource(dtWindHail, "Deductible", "Deductible");
            }

            return output;
        }

        /// <summary>
        /// Execute the MultiRate ReRate SP
        /// </summary>
        /// <param name="currentDeductible">current or old regular deductible</param>
        /// <param name="ratingCaseNumber">current rating case number</param>
        /// <param name="newDeductible">the new (current) deductible</param>
        /// <param name="windHailOrHurricane">wind/hail or hurricane deductible</param>
        /// <param name="covEAmount">Coverage E amount</param>
        /// <param name="covFAmount">Coverage F amount</param>
        /// <param name="incCovCAmount">Increased Coverage C amount</param>
        /// <param name="covAAmount">coverage A amount</param>
        /// <param name="coveredPeril">covered perils flag, 0 or 1</param>
        /// <param name="formCode">form code</param>
        /// <param name="ratedPremium">output of the rated premium for this rerate case</param>
        /// <param name="lastDeductible">the regular deductible used for this rating case</param>
        /// <param name="newRatingCaseNumber">the new rating case number used for this rating case</param>
        private void CallReRate(string currentDeductible, string ratingCaseNumber,
            string newDeductible, string windHailOrHurricane, decimal? covEAmount, decimal? covFAmount, 
            decimal? incCovCAmount, decimal? covAAmount, int coveredPeril, int formCode, 
            out string ratedPremium, out string lastDeductible, out string newRatingCaseNumber)
        {
            ratedPremium = string.Empty;
            newRatingCaseNumber = string.Empty;
            lastDeductible = string.Empty;

            if ((currentDeductible.Trim().Length > 0) && (newDeductible.Trim().Length > 0))
            {
                DataTable dtnewRatedData = DirectWebDAC.GetReRateData(Convert.ToInt32(ratingCaseNumber), 
                    "DEDUCTIBLE",
                    currentDeductible,
                    newDeductible,
                    windHailOrHurricane,
                    covEAmount,
                    covFAmount,
                    incCovCAmount,
                    covAAmount,
                    coveredPeril,
                    formCode);

                if ((dtnewRatedData != null) && (dtnewRatedData.Rows.Count > 0))
                {
                    ratedPremium = dtnewRatedData.Rows[0]["totalratedpremium"].ToString().Trim();
                    newRatingCaseNumber = dtnewRatedData.Rows[0]["Casenumber"].ToString().Trim();
                }
            }

            lastDeductible = newDeductible;
        }

        /// <summary>multiplication process for increased Coverage C value</summary>
        /// <param name="covAAmount">Coverage A amount</param>
        /// <param name="covCAmount">Coverage C amount</param>
        /// <param name="covCPercentValue">Coverage C percentage amount</param>
        /// <returns>(coverage A * coverage C percentage) - Coverage C</returns>
        public double GetIncreasedCoverageCValues(decimal? covAAmount, decimal? covCAmount, double covCPercentValue)
        {
            double cova = covAAmount == null ? 0 : (double)covAAmount;
            double covc = covCAmount == null ? 0 : (double)covCAmount;
            return (cova * covCPercentValue) - covc;
        }

        /// <summary>Gets the key used to save into AdditionalCoverage.AddLimitsLiability, since we only have the multiplier, and coverageA amount
        /// we'll have to do the multiplication and map it to the coverageInput</summary>
        public string GetIncreasedCoverageAKey(double optionMultiplier)
        {
            string val = GetExtendedReplacementCost(optionMultiplier);
            string output = string.Empty;

            switch (val)
            {
                case "0":
                    output = "0";
                    break;
                case "0.25":
                    output = "3";
                    break;
                case "0.5":
                    output = "2";
                    break;
                default: break;
            }

            return output;
        }

        /// <summary>Gets the Key to be saved associated with Increased Coverage C values.</summary>
        /// <param name="option">percentage value from MultiRate configurations (found in partner config files)</param>
        /// <returns>the key associated with the argued percentage value</returns>
        /// <remarks>This is a bad hardcode and should be removed</remarks>
        public string GetIncreasedCoverageCKey(double option)
        {
            string output = null;

            switch (option.ToString())
            {
                case "0.5":
                    output = "2";
                    break;
                case "0.7":
                    output = "3";
                    break;
                default: break;
            }

            return output;
        }

        /// <summary>Multiply the coverage A amount to the increased coverage A percentage</summary>
        /// <param name="covAAmount">Dwelling - Coverage A amount</param>
        /// <param name="covAPercentValue">percentage coming from MultiRateOptionSet</param>
        /// <returns>Coverage A * Coverage A Percentage</returns>
        private decimal? GetIncreasedCoverageAValues(decimal? covAAmount, double covAPercentValue)
        {
            double covA = covAAmount == null ? 0 : (double)covAAmount;
            return (decimal?)(covA * covAPercentValue);
        }

        /// <summary>Get the increased Coverage A value, using the key which represents the percentage value</summary>
        /// <param name="covAAmount">Coverage A amount</param>
        /// <param name="covAIndexValue">Coverage A percentage key</param>
        /// <returns>multiplied increased coverage A value</returns>
        private double GetIncreasedCoverageAValues(decimal? covAAmount, short? covAIndexValue)
        {
            double covAPercentValue = 0;

            switch (covAIndexValue)
            {
                case 0:
                    covAPercentValue = 0; 
                    break;
                case 3:
                    covAPercentValue = 0.25; 
                    break;
                case 2:
                    covAPercentValue = 0.5; 
                    break;
                default: break;
            }

            return (double)GetIncreasedCoverageAValues(covAAmount, covAPercentValue);
        }

        /// <summary>wrapper for the CoveragesDataSourceProvider call</summary>
        /// <returns>IControlDataSource containing all options for extendedReplacementCost</returns>
        private IControlDataSource GetExtendedReplacementCost()
        {
            ICoverageInputData inputData = new ConsumerWebCoverageInputData(this.BusinessLogic(), ConfigurationManagement.StaticCollection.GetPartnerName(this.BusinessLogic().QHeader.PartnerId));
            CoveragesDataSourceProvider provider = new CoveragesDataSourceProvider(inputData);
            return provider.GetExtendedReplacementCost();
        }

        #endregion Privates

        #region IBusinessLogicVisitor Members

        IBusinessLogicVisitable _acceptingClass;
        public void Visit(IBusinessLogicVisitable visitable)
        {
            _acceptingClass = visitable;
        }

        public QuoteData QData
        {
            get { return _acceptingClass.QData; }
            set { _acceptingClass.QData = value; }
        }

        public QuoteHeader QHeader
        {
            get { return _acceptingClass.QHeader; }
            set { _acceptingClass.QHeader = value; }
        }

        public bool IsDnq
        {
            get { return _acceptingClass.IsDnq; }
            set { _acceptingClass.IsDnq = value;}
        }

        public bool IsPurchased
        {
            get { return _acceptingClass.IsPurchased; }
        }

        public bool IsIneligible
        {
            get { return _acceptingClass.IsIneligible; }
            set { _acceptingClass.IsIneligible = value; }
        }

        public bool IsMpq
        {
            get { return _acceptingClass.IsMpq; }
        }

        public bool IsContactCustomerCare
        {
            get { return _acceptingClass.IsContactCustomerCare; }
            set { _acceptingClass.IsContactCustomerCare = value; }
        }

        public IQuote.ABTestGroupCollection ABTestGroups
        {
            get { return _acceptingClass.ABTestGroups; }
            set { _acceptingClass.ABTestGroups = value; }
        }

        public IQuote.AddressBrokerOutputs AddressBrokerOutputs
        {
            get { return _acceptingClass.AddressBrokerOutputs; }
            set { _acceptingClass.AddressBrokerOutputs = value; }
        }

        public IQuote.AddressBrokerOutput PropertyAddressBrokerOutput
        {
            get { return _acceptingClass.PropertyAddressBrokerOutput; }
            set { _acceptingClass.PropertyAddressBrokerOutput = value; }
        }

        public IQuote.People People
        {
            get { return _acceptingClass.People; }
            set { _acceptingClass.People = value; }
        }

        public IQuote.Structure Structure
        {
            get { return _acceptingClass.Structure; }
            set { _acceptingClass.Structure = value; }
        }

        public IQuote.Address MailingAddress
        {
            get { return _acceptingClass.MailingAddress; }
            set { _acceptingClass.MailingAddress = value; }
        }

        public IQuote.Address PriorAddress
        {
            get { return _acceptingClass.PriorAddress; }
            set { _acceptingClass.PriorAddress = value; }
        }

        public IQuote.Address UserMailingAddress
        {
            get { return _acceptingClass.UserMailingAddress; }
            set { _acceptingClass.UserMailingAddress = value; }
        }

        public IQuote.Address UserPropertyAddress
        {
            get { return _acceptingClass.UserPropertyAddress; }
            set { _acceptingClass.UserPropertyAddress = value; }
        }

        public IQuote.Person PrimaryPerson
        {
            get { return _acceptingClass.PrimaryPerson; }
            set { _acceptingClass.PrimaryPerson = value; }
        }

        public IQuote.Person SecondaryPerson
        {
            get { return _acceptingClass.SecondaryPerson; }
            set { _acceptingClass.SecondaryPerson = value; }
        }

        public IQuote.Addresses Addresses
        {
            get { return _acceptingClass.Addresses; }
            set { _acceptingClass.Addresses = value; }
        }

        public IQuote.Address PropertyAddress
        {
            get { return _acceptingClass.PropertyAddress; }
            set { _acceptingClass.PropertyAddress = value; }
        }

        public IQuote.Coverage Coverage
        {
            get { return _acceptingClass.Coverage; }
            set { _acceptingClass.Coverage = value; }
        }

        public IQuote.AdditionalCoverage AdditionalCoverage
        {
            get { return _acceptingClass.AdditionalCoverage; }
            set { _acceptingClass.AdditionalCoverage = value; }
        }

        public IQuote.Quote Quote
        {
            get { return _acceptingClass.Quote; }
            set { _acceptingClass.Quote = value; }
        }

        public IQuote.Header Header
        {
            get { return _acceptingClass.Header; }
            set { _acceptingClass.Header = value; }
        }

        public IQuote.HomeDayCare HomeDayCare
        {
            get { return _acceptingClass.HomeDayCare; }
            set { _acceptingClass.HomeDayCare = value; }
        }

        private PackageCoverages packageCoverages;
        public PackageCoverages PackageCoverages
        {
            set { packageCoverages = value; }
            get
            {
                if (packageCoverages == null)
                {
                    packageCoverages = PackageCoverages.GetPackageCoverages(QHeader.SessionId);
                }

                return packageCoverages;
            }
        }

        #endregion IBusinessLogicVisitor Members

        #region UNUSED

        public IQuote.QualityGroups QualityGroup
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.HomeBusinessDetail HomeBusinessDetail
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.AvailableCredit Credit
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.Animals Animals
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.Claims Claims
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.PaymentDetail Payment
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.CreditCard CreditCard
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public PurchaseData PData
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.EFTs EFTs
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.Endorsements Endorsements
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.FaxDetails FaxDetails
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.HO0448s HO0448s
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.Mortgages Mortgages
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.ScheduledPersonalPropreties ScheduledPersonalPropreties
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.ThankYouDetail ThankYouDetail
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.FloorTypes FloorTypes
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.WallTypes WallTypes
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.FireplaceTypes FireplaceTypes
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public IQuote.AutoClaims AutoClaims
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        #endregion UNUSED
    }
}
