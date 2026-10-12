namespace Muyu.Core;
public sealed record QueueUndo(long Revision,QueueItem[] Before,Guid? Anchor,Guid[] Played);
public static class QueueEditing
{
    public static QueueItem[] RemovePending(IReadOnlyList<QueueItem> queue,int current,IReadOnlySet<Guid> selected)=>queue.Where((item,i)=>i<=current||!selected.Contains(item.ItemId)).ToArray();
    public static QueueItem[] MovePending(IReadOnlyList<QueueItem> queue,int current,IReadOnlySet<Guid> selected,int destination)
    {
        if(destination<=current||destination>queue.Count||queue.Take(current+1).Any(i=>selected.Contains(i.ItemId)))throw new InvalidOperationException("只能调整待播项目");
        var moving=queue.Where(i=>selected.Contains(i.ItemId)).ToArray();int before=queue.Take(destination).Count(i=>selected.Contains(i.ItemId));var remaining=queue.Where(i=>!selected.Contains(i.ItemId)).ToList();remaining.InsertRange(Math.Clamp(destination-before,current+1,remaining.Count),moving);return remaining.ToArray();
    }
    public static QueueItem[] Restore(IReadOnlyList<QueueItem> queue,int current,long revision,QueueUndo undo,IReadOnlySet<Guid> played)
    {
        if(revision!=undo.Revision)throw new InvalidOperationException("队列结构已变化，撤销记录已过期");
        Guid? anchor=current>=0&&current<queue.Count?queue[current].ItemId:null;
        int original=anchor==null?-1:Array.FindIndex(undo.Before,i=>i.ItemId==anchor);
        if(anchor!=null&&original<0)throw new InvalidOperationException("当前曲目不在撤销快照中");
        var prefix=queue.Take(current+1).ToArray();var prefixIds=prefix.Select(i=>i.ItemId).ToHashSet();
        return prefix.Concat(undo.Before.Skip(original+1).Where(i=>!played.Contains(i.ItemId)&&!prefixIds.Contains(i.ItemId))).ToArray();
    }
}
