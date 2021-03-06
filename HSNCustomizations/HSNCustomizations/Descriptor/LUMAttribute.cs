using System;
using System.Collections;
using PX.Common;
using PX.Data;
using PX.Data.BQL.Fluent;
using PX.Objects.AR;
using PX.Objects.IN;
using HSNCustomizations.DAC;
using System.Collections.Generic;
using PX.CS;
using PX.Data.BQL;
using System.Linq;
using PX.Objects.FS;
using PX.Objects.AP;

namespace HSNCustomizations.Descriptor
{
    #region WorkflowRuleSelectorAttribute
    #region enum
    public enum WFRule
    {
        OPEN01 = 0,
        ASSIGN01 = 1,
        ASSIGN03 = 2,
        START01 = 3,
        QUOTATION01 = 4,
        QUOTATION03 = 5,
        AWSPARE01 = 6,
        AWSPARE03 = 7,
        AWSPARE05 = 8,
        AWSPARE07 = 9,
        FINISH01 = 10,
        COMPLETE01 = 11,
        COMPLETE03 = 12,
        INVOICE01 = 13
    }
    #endregion

    public class WorkflowRuleSelectorAttribute : PXCustomSelectorAttribute
    {
        public WorkflowRuleSelectorAttribute() : base(typeof(LUMWorkflowRule.ruleID),
                                                      typeof(LUMWorkflowRule.ruleID),
                                                      typeof(LUMWorkflowRule.descr))
        {
            DescriptionField = typeof(LUMWorkflowRule.descr);
        }

        public override void FieldVerifying(PXCache sender, PXFieldVerifyingEventArgs e) { }

        public static string[] WFRuleDescr =
        {
            "Change to Open Stage when appointment is created",
            "Change to Assigned Stage when staff is assigned",
            "Change to Waiting Stage when driver is arranged to pick up machine",
            "Change to Under Diagnose Stage when appointment is started",
            "Change to Quotation Required Stage when parts is required",
            "Change to Quoted when email is sent to customer",
            "Change to Awaiting Spare Parts Stage when part request is initiated",
            "Change to Under Repair Stage when 1-step transfer is released",
            "Change to Part in Transit Stage when 2-step transfer out is released",
            "Change to Under Repair Stage when 2-step transfer is received and released",
            "Change to Under Testing when Finished Check Box is ticked",
            "Change to Repair Complete when appointment is 'completed' by QC/ Engineer",
            "Change to RTS when service order is 'completed' by",
            "Change to Closed when invoice is generated and released."
        };

        protected virtual IEnumerable GetRecords()
        {
            foreach (string wFRule in Enum.GetNames(typeof(WFRule)))
            {
                LUMWorkflowRule wfRule = new LUMWorkflowRule()
                {
                    RuleID = wFRule,
                    Descr = WFRuleDescr[(int)Enum.Parse(typeof(WFRule), wFRule)]
                };

                yield return wfRule;
            }
        }

        #region Unbound DAC
        [PXHidden]
        [Serializable]
        public partial class LUMWorkflowRule : PX.Data.IBqlTable
        {
            #region RuleID
            [PXString(12, IsUnicode = true, IsKey = true)]
            [PXUIField(DisplayName = "Rule")]
            public virtual string RuleID { get; set; }
            public abstract class ruleID : PX.Data.BQL.BqlString.Field<ruleID> { }
            #endregion

            #region Descr
            [PXString(256, IsUnicode = true)]
            [PXUIField(DisplayName = "Description")]
            public virtual string Descr { get; set; }
            public abstract class descr : PX.Data.BQL.BqlString.Field<descr> { }
            #endregion
        }
        #endregion
    }
    #endregion

    #region ARPymtNumberingAttribute
    /// <summary>
    /// If “Customer Prepayment Numbering Sequence” is blank, follow the standard numbering sequence when the transaction Type is “Prepayment”.
    /// </summary>
    public class ARPymtNumberingAttribute : ARPaymentType.NumberingAttribute
    {
        public override void RowPersisting(PXCache sender, PXRowPersistingEventArgs e)
        {
            string curDoType = (string)sender.GetValue<ARPayment.docType>(e.Row);

            LUMHSNSetup hSNSetup = SelectFrom<LUMHSNSetup>.View.Select(sender.Graph);

            if (curDoType == ARDocType.Prepayment && !string.IsNullOrEmpty(hSNSetup?.CPrepaymentNumberingID) && this.UserNumbering == false && e.Operation == PXDBOperation.Insert)
            {
                string generated = PX.Objects.CS.AutoNumberAttribute.GetNextNumber(sender, e.Row, hSNSetup.CPrepaymentNumberingID, (e.Row as ARPayment).DocDate);

                sender.SetValue(e.Row, _FieldName, generated);
            }
            else
            { base.RowPersisting(sender, e); }
        }
    }
    #endregion

    #region INTotalQtyVerificationAttribute
    /// <summary>
    /// If LUMHSNSetup.EnablePartReqInAppt=True, then if INRegister.TotalQty=0, then display error “System cannot save records with 0 quantity” when user click SAVE button.
    /// This rule should apply to Inventory Receipts, Issue, and Transfer.
    /// </summary>
    public class INTotalQtyVerificationAttribute : PXDBQuantityAttribute, IPXRowPersistingSubscriber
    {
        public override void RowPersisting(PXCache sender, PXRowPersistingEventArgs e)
        {
            string docType = (string)sender.GetValue<INTran.docType>(e.Row);
            decimal totalQty = (decimal)sender.GetValue<INTran.qty>(e.Row);

            if (e.Operation != PXDBOperation.Delete && docType.IsIn(INDocType.Receipt, INDocType.Issue, INDocType.Transfer) &&
                SelectFrom<LUMHSNSetup>.View.Select(sender.Graph).TopFirst?.EnablePartReqInAppt == true && totalQty <= 0)
            {
                sender.RaiseExceptionHandling<INTran.qty>(e.Row, totalQty, new PXSetPropertyException(HSNMessages.TotalQtyIsZero, PXErrorLevel.Error));
            }
        }
    }
    #endregion

    public class LUMCSAttributeListAttribute : PXStringListAttribute
    {
        public string _attribtueID;

        public LUMCSAttributeListAttribute(string attributeID) : base()
        {
            _attribtueID = attributeID;
        }

        public override void CacheAttached(PXCache sender)
        {
            base.CacheAttached(sender);
            var data = SelectFrom<CSAttributeDetail>
                       .Where<CSAttributeDetail.attributeID.IsEqual<P.AsString>>
                       .View.Select(new PXGraph(), this._attribtueID).RowCast<CSAttributeDetail>();
            if (data != null)
            {
                this._AllowedLabels = data.Select(x => x.Description).ToArray();
                this._AllowedValues = data.Select(x => x.ValueID).ToArray();
            }
        }
    }

    #region LUMGetStaffByBranchAttribute
    // [All-Phase2] Add a Control to enable the staff selection by Branch in Appointments
    public class LUMGetStaffByBranchAttribute : PXCustomSelectorAttribute
    {
        public class StaffProviderRec : IBqlTable
        {
            #region BAccountID
            [PXDBInt]
            [PXUIField(DisplayName = "BAccountID", Visibility = PXUIVisibility.SelectorVisible)]
            public virtual int? BAccountID { get; set; }
            public abstract class bAccountID : PX.Data.IBqlField { }

            #endregion

            #region AcctCD
            [PXDBString(128, InputMask = "", IsUnicode = true)]
            [PXUIField(DisplayName = "AcctCD", Visibility = PXUIVisibility.SelectorVisible)]
            public virtual string AcctCD { get; set; }
            public abstract class acctCD : PX.Data.IBqlField { }

            #endregion

            #region AcctName
            [PXDBString(128, InputMask = "", IsUnicode = true)]
            [PXUIField(DisplayName = "AcctName", Visibility = PXUIVisibility.SelectorVisible)]
            public virtual string AcctName { get; set; }
            public abstract class acctName : PX.Data.IBqlField { }
            #endregion

            #region Type
            [EmployeeType.List()]
            [PXDBString(2, IsFixed = true)]
            [PXUIField(DisplayName = "Type", Visibility = PXUIVisibility.SelectorVisible, Enabled = false)]
            public virtual string Type { get; set; }
            public abstract class type : PX.Data.IBqlField { }
            #endregion

            #region Status
            [PXDBString(1, IsFixed = true)]
            [PXUIField(DisplayName = "Status", Visibility = PXUIVisibility.SelectorVisible)]
            public virtual string Status { get; set; }
            public abstract class status : PX.Data.IBqlField { }
            #endregion

            #region PositionID
            [PXDBString(10, IsUnicode = true)]
            [PXUIField(DisplayName = "Position", Visibility = PXUIVisibility.SelectorVisible)]
            public virtual string PositionID { get; set; }
            public abstract class positionID : PX.Data.IBqlField { }
            #endregion

        }

        public LUMGetStaffByBranchAttribute() : base(
                           typeof(StaffProviderRec.bAccountID),
                           new Type[]
            {
                           typeof(StaffProviderRec.acctCD),
                           typeof(StaffProviderRec.acctName),
                           typeof(StaffProviderRec.type),
                           typeof(StaffProviderRec.status),
                           typeof(StaffProviderRec.positionID)
            })
        {
            DescriptionField = typeof(StaffProviderRec.acctName);
            SubstituteKey = typeof(StaffProviderRec.acctCD);
        }

        public override void FieldVerifying(PXCache sender, PXFieldVerifyingEventArgs e) { }

        protected virtual IEnumerable GetRecords()
        {
            #region Standard BQL (Select Staff)
            PXSelectBase<BAccountStaffMember> staffSelect =
                   new PXSelectJoin<BAccountStaffMember,
                          LeftJoin<Vendor,
                          On<
                              Vendor.bAccountID, Equal<BAccountStaffMember.bAccountID>,
                           And<Vendor.vStatus, NotEqual<VendorStatus.inactive>>>,
                          LeftJoin<PX.Objects.EP.EPEmployee,
                          On<
                              PX.Objects.EP.EPEmployee.bAccountID, Equal<BAccountStaffMember.bAccountID>,
                              And<PX.Objects.EP.EPEmployee.vStatus, NotEqual<VendorStatus.inactive>>>,
                          LeftJoin<PX.Objects.PM.PMProject,
                          On<
                              PX.Objects.PM.PMProject.contractID, Equal<Current<FSServiceOrder.projectID>>>,
                          LeftJoin<PX.Objects.EP.EPEmployeeContract,
                          On<
                              PX.Objects.EP.EPEmployeeContract.contractID, Equal<PX.Objects.PM.PMProject.contractID>,
                              And<PX.Objects.EP.EPEmployeeContract.employeeID, Equal<BAccountStaffMember.bAccountID>>>,
                          LeftJoin<PX.Objects.EP.EPEmployeePosition,
                          On<
                              PX.Objects.EP.EPEmployeePosition.employeeID, Equal<PX.Objects.EP.EPEmployee.bAccountID>,
                              And<PX.Objects.EP.EPEmployeePosition.isActive, Equal<True>>>>>>>>,
                          Where<
                              PX.Objects.PM.PMProject.isActive, Equal<True>,
                          And<
                              PX.Objects.PM.PMProject.baseType, Equal<PX.Objects.CT.CTPRType.project>,
                          And<
                              Where2<
                                  Where<
                                      FSxVendor.sDEnabled, Equal<True>>,
                                  Or<
                                      Where<
                                          FSxEPEmployee.sDEnabled, Equal<True>,
                                      And<
                                          Where<
                                              PX.Objects.PM.PMProject.restrictToEmployeeList, Equal<False>,
                                          Or<
                                              PX.Objects.EP.EPEmployeeContract.employeeID, IsNotNull>>>>>>>>>,
                          OrderBy<
                              Asc<BAccountStaffMember.acctCD>>>(this._Graph);
            #endregion

            // Appointment Record
            var apppintmentRecord = this._Graph.Caches[typeof(FSAppointment)].Current as FSAppointment;
            // Appointment Branch LocationID
            var branchLocationID = FSServiceOrder.PK.Find(this._Graph, apppintmentRecord?.SrvOrdType, apppintmentRecord?.SORefNbr)?.BranchLocationID;
            // BranchLocationID 實際的BranchID
            var currentBranchID = FSBranchLocation.PK.Find(this._Graph, branchLocationID)?.BranchID;
            // 是否篩選Staff
            var isFilter = apppintmentRecord == null ? false : (FSSrvOrdType.PK.Find(this._Graph, apppintmentRecord.SrvOrdType)?.GetExtension<FSSrvOrdTypeExt>()?.UsrStaffFilterByBranch ?? false);
            Dictionary<string, string> statusDic = new Dictionary<string, string>()
            {
                {"R","Prospect"},
                {"A","Active" },
                {"H","Hold" },
                {"C","CreditHold" },
                {"T","OneTime" },
                {"I","Inactive" }
            };
            foreach (PXResult<BAccountStaffMember, Vendor, PX.Objects.EP.EPEmployee, PX.Objects.PM.PMProject, PX.Objects.EP.EPEmployeeContract, PX.Objects.EP.EPEmployeePosition> it in staffSelect.Select())
            {
                BAccountStaffMember staff = it;
                PX.Objects.EP.EPEmployeePosition post = it;
                PX.Objects.EP.EPEmployee employee = it;
                var staffBranchID = SelectFrom<PX.Objects.GL.Branch>
                               .Where<PX.Objects.GL.Branch.bAccountID.IsEqual<P.AsInt>>
                               .View.SelectSingleBound(this._Graph, null, employee.ParentBAccountID)
                               .TopFirst?.BranchID;

                // 只回傳Branch ID相同的(需篩選)
                if (apppintmentRecord != null && isFilter)
                {
                    if (staffBranchID == currentBranchID)
                        yield return new StaffProviderRec
                        {
                            AcctCD = staff.AcctCD,
                            BAccountID = staff.BAccountID,
                            AcctName = staff.AcctName,
                            Type = staff.Type,
                            Status = statusDic[staff.Status],
                            PositionID = post.PositionID
                        };
                }
                // 如果找不到Appointment header or 不需要篩選則回傳標準
                else
                    yield return new StaffProviderRec
                    {
                        AcctCD = staff.AcctCD,
                        BAccountID = staff.BAccountID,
                        AcctName = staff.AcctName,
                        Type = staff.Type,
                        Status = statusDic[staff.Status],
                        PositionID = post.PositionID
                    };
            }
        }
    }
    #endregion
}
