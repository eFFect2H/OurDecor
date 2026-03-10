using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Models
{
    public class ApplicationSummaryRow
    {
        public string Object { get; set; }
        public string Direction { get; set; }
        public int Total { get; set; }
        public int Open { get; set; }
        public int Closed { get; set; }

        public double? AvgCloseHours { get; set; }
        public int OldestOpenDays { get; set; }
    }

}
