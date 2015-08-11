using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ToDoModel
{
    public class ToDoListData
    {
        public int? Id { get; set; }
        public DateTime? DateTimeStamp { get; set; }
        public string Text { get; set; }
        public string Title { get; set; }
    }
}
