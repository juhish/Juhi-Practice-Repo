using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ToDoModel;

using System.Data;
using System.Data.SqlClient;

using System.Configuration;

namespace ToDoBusinesslayer
{
   public class ToDoLogic
   {
       #region Configuration Data
       private static string sp_ReadTodoList = "dbo.ReadToDoList";
       private static string sp_UpdateTodoList = "dbo.UpdToDoList";
       private static string sp_DeleteTodoList = "dbo.DelToDoList";
       private static string sp_InsertTodoList = "dbo.insertToDoData";
       private static string getConnectionString()
       {
           return ConfigurationSettings.AppSettings["SQLConnectionString"];
       }
       #endregion

       // Insert new To Do Data in Database
       public bool insertToDoData(ToDoListData data){
           //NonQuery

           using (var connObj = new SqlConnection(getConnectionString())) {
               using (var cmdObj = new SqlCommand()) {
                   cmdObj.CommandText = sp_InsertTodoList;
                   cmdObj.CommandType = CommandType.StoredProcedure;

                   var parameters = new []{
                   new SqlParameter("@Text",data.Text),
                   new SqlParameter("@Title",data.Title)
                   };

                   foreach(var param in parameters)
                   cmdObj.Parameters.Add(param);

                   cmdObj.Connection = connObj;
                   try{
                   connObj.Open();
                    cmdObj.ExecuteNonQuery();
                       return true;
                   
                   }
                   catch(Exception ex){
                  throw ex;
                   }
                   

               }
           
           }

          


           

       }
       //  Read All Data from database
       public DataSet readToDoData()
       {
           using (var connObj = new SqlConnection(getConnectionString()))
           {
               using (var cmdObj = new SqlCommand())
               {
                   cmdObj.CommandText = sp_ReadTodoList;
                   cmdObj.CommandType = CommandType.StoredProcedure;


                   cmdObj.Connection = connObj;
                   try
                   {
                       connObj.Open();
                       var ds = new DataSet();
                       var dataAdapter = new SqlDataAdapter();
                       dataAdapter.SelectCommand = cmdObj;
                       dataAdapter.Fill(ds);

                       return ds;

                   }
                   catch (Exception ex)
                   {
                       throw ex;
                   }


               }
           }
       }
       //Update Data
       public bool updateToDoData(int id,ToDoListData data)
       {
           return true;
       }
       //Delete Data
       public bool deleteToDoData(int ids)
       {
           return true;
       }
    }
}
