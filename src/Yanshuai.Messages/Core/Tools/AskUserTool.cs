using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Yanshuai.Messages.Core
{
    public sealed class AskUserTool : ToolBase
    {
        public override string Name { get { return "ask_user"; } }
        public override string Description { get { return "向用户请求缺失信息或结构化输入，等待用户回答后继续。"; } }
        public override ToolGroup Group { get { return ToolGroup.Agent; } }
        protected override void DescribeParams(ToolSchema schema)
        {
            schema.Param("question", "string", "单个完整问题", false);
            schema.Param("options", "string", "可选答案的 JSON 字符串数组", false);
            schema.Param("questions", "string", "结构化问题的 JSON 数组；每项 id、text、type(text/number/date/boolean)、options、multiple、freeText", false);
        }
        public override async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext context, CancellationToken ct)
        {
            if (context.Interaction == null) return ToolResult.Failure("问答界面不可用。");
            var questions = new List<AskQuestion>();
            var structured = ToolArguments.GetString(argumentsJson,"questions","");
            try {
                if (!string.IsNullOrWhiteSpace(structured)) {
                    var ids = new HashSet<string>();
                    foreach (var value in JArray.Parse(structured)) {
                        if (questions.Count >= 8) return ToolResult.Failure("一次最多提 8 个问题。");
                        var item = value as JObject;
                        if (item == null) return ToolResult.Failure("每个问题必须是 JSON 对象。");
                        string id = (string)item["id"] ?? "q" + questions.Count;
                        string text = (string)item["text"];
                        if (string.IsNullOrWhiteSpace(text) || !ids.Add(id)) return ToolResult.Failure("问题需有正文且 id 不可重复。");
                        string kind = (string)item["type"] ?? "text";
                        if (kind!="text"&&kind!="number"&&kind!="date"&&kind!="boolean")return ToolResult.Failure("不支持的问题类型。");
                        var question = new AskQuestion { Id=id,Text=text,InputType=kind,
                            AllowMultiple=(bool?)item["multiple"]??false,AllowFreeText=(bool?)item["freeText"]??true };
                        var options=item["options"] as JArray;
                        if(options!=null)foreach(var option in options)if(question.Options.Count<10)question.Options.Add((string)option);
                        if(kind=="boolean"){question.Options.Clear();question.Options.Add("是");question.Options.Add("否");question.AllowFreeText=false;question.AllowMultiple=false;}
                        questions.Add(question);
                    }
                } else {
                    string text = ToolArguments.GetString(argumentsJson,"question","");
                    if(string.IsNullOrWhiteSpace(text))return ToolResult.Failure("问题不能为空。");
                    var question=new AskQuestion { Id="answer",Text=text,AllowFreeText=true };
                    foreach(var option in JArray.Parse(ToolArguments.GetString(argumentsJson,"options","[]")))
                        if(question.Options.Count<10)question.Options.Add((string)option);
                    questions.Add(question);
                }
            } catch(Exception){return ToolResult.Failure("请提供有效的问题与选项 JSON。");}
            if(questions.Count==0)return ToolResult.Failure("问题不能为空。");
            var answers=await context.Interaction.AskAsync(questions,ct).ConfigureAwait(false);
            if(answers==null||answers.Count==0)return ToolResult.Failure("用户取消了问答。");
            var result=new JObject();
            foreach(var answer in answers)result[answer.Id]=answer.Joined;
            return ToolResult.Success(result.ToString());
        }
    }
}
