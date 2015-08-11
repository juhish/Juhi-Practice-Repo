using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using ToDoModel;
using ToDoBusinesslayer;

using System.Data;

using Newtonsoft.Json;


namespace To.Controllers
{
    public class ToDoController : ApiController
    {
        // GET api/ToDo
        public string Get()
        {
            var result = new ToDoLogic().readToDoData();
            return JsonConvert.SerializeObject(result);
           
        }

        // GET api/<controller>/5
        public string Get(int id)
        {
            return "value";
        }
        
        // POST api/<controller>
        public void Post([FromBody]ToDoListData value)
        {
            new ToDoLogic().insertToDoData(value);
        }

        // PUT api/<controller>/5
        public void Put(int id, [FromBody]ToDoListData value)
        {
            new ToDoLogic().updateToDoData(id,value);
        }

        // DELETE api/<controller>/5
        public void Delete(int id)
        {
            new ToDoLogic().deleteToDoData(id);
        }
    }
}