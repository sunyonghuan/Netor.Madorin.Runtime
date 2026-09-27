using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;

internal static partial class Smoke
{
    private static async Task TimedAsync()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "RealModeSmoke.dll");
        var first = System.Text.Json.JsonSerializer.Serialize(new { fileName = "dotnet", arguments = new[]
            { helper, "--timed-read", Path.Combine(_workspace, "sales.csv"), "A", Path.Combine(_workspace, "timing-a.json") } });
        var second = System.Text.Json.JsonSerializer.Serialize(new { fileName = "dotnet", arguments = new[]
            { helper, "--timed-read", Path.Combine(_workspace, "refunds.csv"), "B", Path.Combine(_workspace, "timing-b.json") } });
        var selection = Select(RuntimeMode.Expert, new ExpertModeOptions(Agent("timer",
            "你是工具并发测试专家。严格原样使用用户给出的两个JSON参数，在同一次模型响应中发出两个mcli_process调用；不能分轮，不能合并到一个命令。收到全部工具结果后简洁报告。")));
        await TurnAsync("E7-timed-parallel-tools", selection,
            "请在同一轮并行调用两次mcli_process。两个任务各自真实读取一个CSV，内部固定耗时2秒且记录起止时间。参数A：" + first + "\n参数B：" + second + "\n工具结果返回后，报告两次调用的起止时间和是否重叠，不要只凭发出顺序判断并发。");
    }

    private static async Task ExpertAsync()
    {
        var expert = Agent("expert", "你是中文财务专家。严格按用户指令操作，只根据真实工具结果回答。文件操作必须调用工具，禁止声称未执行的工作已完成。独立读取应在同一轮并行调用工具。回复简洁，保留会话上下文。");
        var selection = Select(RuntimeMode.Expert, new ExpertModeOptions(expert));
        var session = await TurnAsync("E1-memory", selection,
            "本次核算代号是冒烟-青竹915，币种是人民币。先记住这些约定，只回复代号和币种，不调用工具。");
        session = await TurnAsync("E2-parallel-read", selection,
            "沿用刚才的核算代号。请在同一轮并行调用两次 mcli_fs_read，分别读取 sales.csv 和 refunds.csv，两次读取互不依赖。根据真实数据报告订单数量、销售总额、退款总额、净额，并注明代号及币种。", session);
        session = await TurnAsync("E3-write-verify", selection,
            "在刚才结果基础上增加100元退款。用 mcli_fs_write 写 expert-report.md，含核算代号、总销售、总退款、净额；随后再次调用 mcli_fs_read 读回核验。最后用一句中文报告实际写入结果。", session);
        await TurnAsync("E4-process", selection,
            "用 mcli_process 执行 fileName=dotnet，arguments=[\"--version\"]。输出真实SDK版本，并回答本次核算代号和最新净额。", session);
        await Task.WhenAll(
            TurnAsync("E5-concurrent-a", selection, "读取 sales.csv，报告A001金额。必须使用文件工具。"),
            TurnAsync("E6-concurrent-b", selection, "读取 refunds.csv，报告R001金额。必须使用文件工具。"));
    }

    private static NextTurnSelection WorkSelection()
    {
        var manager = Agent("gm", """
            你是总经理，先与老板讨论并澄清需求；没有明确确认前不执行文件写入。
            只有老板明确批准执行时才生成机器可读工作计划，输出纯JSON，结构为：
            {"planVersion":"1","goal":"目标","steps":[{"stepId":"s1","goal":"明确工作与验收标准","targetAgentId":"worker","dependsOn":[],"depth":0}]}。
            可用员工worker负责读写分析，reviewer负责核验和总结。严格区分规划、执行、总结。
            按老板明确要求的步骤与依赖拆分，不遗漏步骤，不虚构完成。目标中写清文件名、输入和应交付内容。
            未批准时正常讨论并询问，不生成可执行计划。发生新需求时保留已完成成果，重新商定计划。
            """);
        var worker = Agent("worker", "你是执行员工，严格按当前步骤目标执行，实际调用文件工具读写，不要只描述操作或提出计划。不得执行后续步骤。完成后简洁报告文件与核验结果。");
        var reviewer = Agent("reviewer", "你是验收员工，实际调用文件工具读取产物并核验数字，按步骤要求写总结文件。报告每一步是否完成，不编造。");
        return Select(RuntimeMode.Work, new WorkModeOptions(manager, [worker, reviewer],
            new WorkflowPolicy(MaxConcurrentSteps: 2, StepTimeoutSeconds: 120, MaxRetriesPerStep: 0)));
    }

    private static async Task WorkAsync()
    {
        var selection = WorkSelection();
        await DiscussWorkAsync(selection);
        await ApprovedWorkAsync(selection);
        await InterruptedWorkAsync(selection);
    }

    private static async Task WorkConfirmAsync()
    {
        var selection = WorkSelection();
        var session = await TurnAsync("WC1-discuss", selection,
            "总经理，先讨论销售报告步骤。只用三句话说明：读取sales.csv、计算总额、写work-confirmed.md。我尚未确认，禁止开始执行任何步骤。不要调用工具。");
        await TurnAsync("WC2-confirm", selection,
            "确认执行。请输出裸JSON计划，交给worker一个步骤：实际读取sales.csv，计算总销售额，写work-confirmed.md并读回核验，然后总结。不要再讨论需求。", session);
    }

    private static NextTurnSelection WorkSetPlanSelection()
    {
        var manager = new AgentRef(
            "gm",
            "smoke-v1",
            """
            你是总经理。制定工作计划时必须调用工具 mcli_work_set_plan，禁止只在回复里输出裸JSON冒充已发布计划。
            set_plan 参数使用 WorkPlanDraft：planVersion/goal/steps；steps 含 stepId、goal、targetAgentId、dependsOn、depth。
            可用员工：worker（读写分析）、reviewer（核验总结）。
            未获明确「确认执行/批准执行」前只讨论或只发布计划，不要执行文件写入。
            需要落盘计划时调用 mcli_work_set_plan，然后等待 Runtime 确认门；不要假装步骤已完成。
            """,
            _provider,
            _model,
            AllowedToolIds:
            [
                "mcli_work_set_plan",
                "mcli_work_list_plan_templates",
                "mcli_work_load_plan_from_template",
                "mcli_work_read_template_file",
                "mcli_fs_read",
                "mcli_fs_write",
                "mcli_fs_inspect",
                "mcli_fs_list"
            ]);
        var worker = Agent("worker", "你是执行员工，严格按当前步骤目标执行，实际调用文件工具读写，不要只描述操作。完成后简洁报告。");
        var reviewer = Agent("reviewer", "你是验收员工，实际调用文件工具读取产物并核验数字，写总结文件。不编造。");
        return Select(
            RuntimeMode.Work,
            new WorkModeOptions(
                manager,
                [worker, reviewer],
                new WorkflowPolicy(
                    MaxConcurrentSteps: 2,
                    StepTimeoutSeconds: 180,
                    MaxRetriesPerStep: 0,
                    RequirePlanConfirmation: true)));
    }

    private static async Task WorkSetPlanAsync()
    {
        var selection = WorkSetPlanSelection();
        var session = await TurnAsync("SP1-discuss", selection,
            "总经理，先讨论：读取sales.csv计算总额并写 work-setplan.md。现在只讨论三句话，禁止调用任何工具，禁止set_plan。");
        session = await TurnAsync("SP2-set-plan", selection,
            """
            现在请调用 mcli_work_set_plan 发布工作计划，禁止只输出裸JSON。这不是执行批准。
            计划要求：planVersion=1；一个步骤 s1，targetAgentId=worker；
            目标：实际读取 sales.csv，计算销售总额，写 work-setplan.md 并读回核验后简短总结。
            本轮只发布计划后停住；禁止执行任何步骤，禁止写文件。
            """, session);
        await TurnAsync("SP3-confirm-execute", selection,
            "确认执行刚才 set_plan 发布的计划。不要再讨论，不要再调用 set_plan，直接按已发布计划执行 s1。", session);
    }

    private static NextTurnSelection WorkAskPendingSelection()
    {
        var manager = new AgentRef(
            "gm",
            "smoke-v1",
            """
            你是总经理。本轮只做交互工具验证，禁止 set_plan，禁止写文件。
            必须先调用 mcli_check_pending_user_input；拿到结果后再调用 mcli_ask_user。
            ask_user 的 question 固定为：本次冒烟是否按当前目标继续？
            收到用户答复后，用一句话中文复述答案，然后结束。不要编造答案。
            """,
            _provider,
            _model,
            AllowedToolIds:
            [
                "mcli_check_pending_user_input",
                "mcli_ask_user"
            ]);
        var worker = Agent("worker", "你是执行员工，本场景不会被调度。");
        return Select(
            RuntimeMode.Work,
            new WorkModeOptions(
                manager,
                [worker],
                new WorkflowPolicy(
                    MaxConcurrentSteps: 1,
                    StepTimeoutSeconds: 120,
                    MaxRetriesPerStep: 0,
                    RequirePlanConfirmation: true)));
    }

    private static async Task WorkAskPendingAsync()
    {
        var selection = WorkAskPendingSelection();
        await TurnAsync("AP1-check-ask", selection,
            """
            请严格按顺序调用工具：
            1) mcli_check_pending_user_input
            2) mcli_ask_user，question=本次冒烟是否按当前目标继续？
            然后用一句话复述宿主返回的答案。禁止 set_plan，禁止写文件。
            """);
    }

    private static async Task DiscussWorkAsync(NextTurnSelection selection)
    {
        var discussion = await TurnAsync("W1-discuss", selection,
            "总经理，我想做一份销售和退款分析报告。请先与我探讨：你建议报告包含哪些指标，按什么步骤做？目前只讨论，未批准计划；禁止调用写入工具或开始执行步骤。");
        discussion = await TurnAsync("W2-refine", selection,
            "增加按市场拆分，并把退款率放进去。先给我修订后的步骤，等待我明确批准。现在不要执行。", discussion);
        await TurnAsync("W3-confirm", selection,
            "确认按讨论后的步骤执行。数据在sales.csv与refunds.csv，最终写work-discussed-report.md并读回核验。全部步骤完成后总结。", discussion);
    }

    private static async Task ApprovedWorkAsync(NextTurnSelection selection)
    {
        await TurnAsync("W4-approved-workflow", selection, """
            我已明确批准以下三个步骤，请完整执行，不用再次确认。总经理输出符合协议的三步JSON计划，然后由员工执行。
            s1：worker实际读取sales.csv，计算销售总额，写work-sales.md；无依赖。
            s2：worker实际读取refunds.csv，计算退款总额，写work-refunds.md；无依赖。s1和s2互不依赖，请并发执行。
            s3：reviewer在s1和s2都完成后，实际读取两个产物，写work-final.md，报告销售2000元、退款150元、净额1850元和每一步验收结果，再读回确认并总结。
            """);
    }

    private static async Task InterruptedWorkAsync(NextTurnSelection selection, bool explicitRevision = false)
    {
        var interrupted = await TurnAsync("W5-interrupt", selection, """
            已确认执行以下三步，必须严格串行，每一步都实际调用文件工具。
            i1：worker读取sales.csv，把销售总额写interrupt-step1.md。
            i2：worker依赖i1，读取refunds.csv，把退款总额写interrupt-step2.md。
            i3：reviewer依赖i2，读取上述两文件，写interrupt-final.md并总结。
            总经理输出三步JSON计划。
            """, cancelAfterStep: true);
        if (explicitRevision)
        {
            await TurnAsync("W8-explicit-revision2", selection,
                "已确认新的需求和执行计划。此会话已经发布planVersion=1，必须生成planVersion=2且previousPlanVersion=1的新计划（两个字段的值均为字符串），禁止再使用版本1。第一步interrupt-step1.md已经完成，不能重做。新版仅两个剩余步骤：r2-write由worker实际读取interrupt-step1.md和refunds.csv，把退款增加100元，计算销售2000、退款250、净额1750，写interrupt-revised.md；r2-check由reviewer依赖r2-write读回验收并总结。只输出裸JSON工作计划，由员工实际执行。", interrupted);
            return;
        }
        var rediscussion = await TurnAsync("W6-rediscuss-after-interrupt", selection,
            "先停下，需求改变了：退款额还要增加100元，最终文件改为interrupt-revised.md。先与我讨论怎么修订计划，保留第一步已有成果，不要重做，不要执行，等待我确认。", interrupted);
        await TurnAsync("W7-confirm-revised", selection,
            "确认新计划，继续执行剩余步骤：退款150+100=250元，净额1750元，写interrupt-revised.md并读回验收。保留第一步成果，不重复执行已完成步骤，最后总结。", rediscussion);
    }

    private static async Task MeetingPauseResumeAsync()
    {
        var host = new AgentRef(
            "host",
            "smoke-v1",
            """
            你是会议主持人。调度阶段只输出裸JSON，可用participantId仅finance与risk。
            示例：{"conclude":false,"participantId":"finance"}。续跑后先吸收老板插话再点名一次，然后{"conclude":true,"participantId":null}。
            禁止调用工具。
            """,
            _provider,
            _model,
            AllowedToolIds: []);
        var finance = Agent("finance", "你是财务。第一次发言调用mcli_fs_read读sales.csv，用真实数字简短建议，不超过80字。");
        var risk = Agent("risk", "你是风控。第一次发言调用mcli_fs_read读refunds.csv，简短评价风险，不超过80字。");
        var options = new MeetingModeOptions(
            [
                new MeetingParticipant("finance", finance, "财务", JoinOrder: 0),
                new MeetingParticipant("risk", risk, "风控", JoinOrder: 1)
            ],
            hostAgent: host,
            selectorPolicy: new MeetingSelectorPolicyOptions(MeetingSelectorPolicy.HostDriven, MaxRounds: 4),
            policy: new MeetingPolicy(
                SummaryMode: MeetingSummaryMode.FinalOnly,
                Summarizer: host,
                HitlEnabled: false,
                RequireEndConfirmation: true,
                InvocationTimeoutSeconds: 120,
                MaxRetriesPerInvocation: 0,
                HostFailure: MeetingHostFailurePolicy.Fail,
                ParticipantFailure: MeetingParticipantFailurePolicy.Skip));
        var selection = Select(RuntimeMode.Meeting, options);

        // Arm pause on first participant invocation of the first run.
        _pauseMeetingOnParticipant = true;
        var session = await TurnAsync(
            "MP1-running-then-pause",
            selection,
            "开会：依据sales.csv与refunds.csv讨论是否推广。主持人点名财务与风控各至少一次。");
        if (session is null)
        {
            Save("MP1-no-session.json", new { error = "pause scenario did not produce a session id" });
            return;
        }

        MeetingResumeResult? pauseStatus = null;
        try
        {
            pauseStatus = await _client.ResumeMeetingAsync(
                new MeetingResumeRequest(session, Guid.NewGuid().ToString("N")));
            Save("MP2-resume-check.json", pauseStatus);
        }
        catch (Exception ex)
        {
            Save("MP2-resume-check.json", new { type = ex.GetType().Name, ex.Message });
        }

        if (pauseStatus is null
            || !string.Equals(pauseStatus.Status, "ReadyForContinuation", StringComparison.Ordinal))
        {
            // Meeting may still be Running if pause raced; force pause then re-check.
            try
            {
                await _client.PauseMeetingAsync(new MeetingPauseRequest(
                    session, Guid.NewGuid().ToString("N"), "smoke force pause"));
            }
            catch (Exception ex)
            {
                Save("MP2-force-pause.json", new { type = ex.GetType().Name, ex.Message });
            }

            // Wait for the active run to park at a safe point.
            await Task.Delay(3000);
            pauseStatus = await _client.ResumeMeetingAsync(
                new MeetingResumeRequest(session, Guid.NewGuid().ToString("N")));
            Save("MP2-resume-check-retry.json", pauseStatus);
        }

        await _client.EnqueueMeetingInputAsync(new MeetingInputEnqueueRequest(
            session,
            Guid.NewGuid().ToString("N"),
            [new TextContentBlock("老板插话：预算上限50元，退款按250元情景讨论后再结束。")]));

        await TurnAsync(
            "MP3-continuation",
            selection,
            "请主持人吸收插话后继续调度，充分后结束讨论并生成总结；结束确认时我再批准。",
            session);
    }

    private static async Task MeetingAsync(bool strict = false, bool interrupt = false, bool controlled = false)
    {
        var host = Agent("host", """
            你是会议主持人，以中文主持老板、财务和风控讨论。按协议选择发言者，安排finance先读数据分析收益、risk再读退款数据讨论风险。
            后续请参会者回应不同意见，至少安排四次参会发言。老板不是可点名参会者。
            你必须吸收老板的预算上限等补充。讨论充分后总结，再明确征询老板是否可以结束；老板明确同意后才正式结束。
            选择发言者时只输出裸JSON对象，例如{"participantId":"finance","conclude":false}或{"conclude":true}，禁止Markdown代码围栏、禁止解释或前后缀；可用participantId只有finance和risk。
            总结时用简短中文列出数字、分歧、决议与行动项。
            """);
        if (strict)
        {
            host = host with { SystemPrompt = host.SystemPrompt + "\n发言选择必须只输出裸JSON对象，例如{\"participantId\":\"finance\",\"conclude\":false}，禁止Markdown代码围栏、禁止解释或前后缀。可用participantId只有finance和risk。总结阶段输出中文总结。" };
        }
        if (controlled)
        {
            host = host with { SystemPrompt = "你是会议主持人。主持调度阶段只输出裸JSON；只能选择finance和risk，前四轮交替点名，示例{\"participantId\":\"finance\",\"conclude\":false}。总结会由单独Summarizer调用完成，所以调度阶段严禁自然语言总结或征询。若老板已明确说可以结束会议，只输出{\"conclude\":true}。在Summarizer阶段用中文总结实际讨论、数字和行动项，并征询老板是否结束；不得编造模型未说过的内容。" };
        }
        var finance = Agent("finance", "你是财务参会者。第一次发言必须调用mcli_fs_read读取sales.csv与refunds.csv，再用真实数字提出建议；后续发言回应其他人的意见，遵守老板的预算上限。每次不超过150字。");
        var risk = Agent("risk", "你是风控参会者。第一次发言必须调用mcli_fs_read读取refunds.csv。评价退款风险，与财务意见有针对性地讨论，提出可执行风控要求，遵守老板预算上限。每次不超过150字。");
        var options = new MeetingModeOptions(
            [new MeetingParticipant("finance", finance, "财务", JoinOrder: 0), new MeetingParticipant("risk", risk, "风控", JoinOrder: 1)],
            hostAgent: host,
            selectorPolicy: new MeetingSelectorPolicyOptions(MeetingSelectorPolicy.HostDriven, MaxRounds: interrupt ? 2 : 4),
            policy: new MeetingPolicy(SummaryMode: MeetingSummaryMode.FinalOnly, Summarizer: host,
                HitlEnabled: true, HitlTimeoutSeconds: 60, InvocationTimeoutSeconds: 120,
                MaxRetriesPerInvocation: 0,
                RequireEndConfirmation: true));
        var selection = Select(RuntimeMode.Meeting, options);
        var session = await TurnAsync(interrupt ? "MI1-running-interrupt" : "M1-discussion", selection,
            "开会讨论：依据sales.csv和refunds.csv决定是否扩大跨境销售推广。请主持人安排财务与风控有序讨论，必要时调用工具核实信息。先讨论收益，再讨论退款风险和试投预算，至少四次发言，最后总结并询问我是否结束，等待我明确答复。当前没有同意结束。");
        if (interrupt)
        {
            await TurnAsync("MI2-after-cancel", selection,
                "老板补充：预算上限50元，退款由150元增加100元变为250元。请主持人吸收变更，财务和风控继续讨论，最后修订总结，征询我能否结束。", session);
            return;
        }
        session = await TurnAsync("M2-continue-topic", selection,
            "先不要结束。我想继续讨论退款额增加100元后的风险，预算上限改为50元。请财务重新核算、风控回应，主持人修订总结，再征求我是否结束。", session);
        await TurnAsync("M3-user-end", selection,
            "认可修订后的结论，可以结束会议。请归档最终纪要，不要再启动新的讨论轮次。", session);
    }
}
