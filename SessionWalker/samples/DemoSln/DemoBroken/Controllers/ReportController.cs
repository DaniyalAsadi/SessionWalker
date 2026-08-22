using System.Web.Mvc;
using EPPlus;          // CS0246: type or namespace not found (reference missing)
using Telerik.Mvc;    // CS0246: type or namespace not found (reference missing)

namespace DemoBroken.Controllers
{
    public class ReportController : Controller
    {
        public void Export()
        {
            EPPlus.ExcelPackage package = null;
            var grid = new Telerik.Mvc.Grid<string>();
        }
    }
}
